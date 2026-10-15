using System.Buffers;
using System.Runtime.InteropServices;
using StorageGO;

internal static class PoolChecks
{
    internal static async Task<(int Total, int Failures)> RunAsync(string directory, IntPtr fixture,
        Func<ulong> clientCount, Func<ulong> transferCount)
    {
        var arm = Marshal.GetDelegateForFunctionPointer<FixtureAction>(NativeLibrary.GetExport(fixture, "StorageGO_FixtureArmIOGate"));
        var release = Marshal.GetDelegateForFunctionPointer<FixtureAction>(NativeLibrary.GetExport(fixture, "StorageGO_FixtureReleaseIOGate"));
        var slowCount = Marshal.GetDelegateForFunctionPointer<FixtureCount>(NativeLibrary.GetExport(fixture, "StorageGO_FixtureSlowCallCount"));
        var data = new byte[512 * 1024 + 13];
        new Random(417).NextBytes(data);
        var source = Path.Combine(directory, "pool-source.bin");
        File.WriteAllBytes(source, data);

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("pool is lazy and sync/async ISmbClient operations reuse a connection", async () =>
            {
                await using var pool = NewPool(maxClients: 4);
                ISmbClient client = pool;
                Check(clientCount() == 0, "Pool eagerly created native clients.");
                var path = Path.Combine(directory, "pool-roundtrip.bin");
                client.Write(path, data);
                Check(client.Read(path).Content.AsSpan().SequenceEqual(data), "Pooled sync content differs.");
                await client.WriteAsync(path, data.AsMemory(), new SmbWriteOptions { Overwrite = true });
                Check((await client.ReadAsync(path)).Content.AsSpan().SequenceEqual(data), "Pooled async content differs.");
                using var destination = new MemoryStream();
                await client.CopyToAsync(path, destination);
                Check(destination.ToArray().AsSpan().SequenceEqual(data), "Pooled async copy differs.");
                destination.SetLength(0);
                client.CopyTo(path, destination);
                using var upload = new MemoryStream(data, writable: false);
                client.Write(path, upload, new SmbWriteOptions { Overwrite = true });
                upload.Position = 0;
                await client.WriteAsync(path, upload, new SmbWriteOptions { Overwrite = true });
                Check(upload.CanRead && destination.CanWrite, "Pool closed an external stream.");
                var stats = pool.GetStatistics();
                Check(stats.ClientsCreated == 1 && stats.IdleClients == 1 && stats.ActiveLeases == 0, "Connection was not reused.");
                Check(data.AsSpan().SequenceEqual(File.ReadAllBytes(source)), "Caller-owned source was changed.");
            }),
            ("pool bounds queued acquires and cancellation starts no file operation", async () =>
            {
                await using var pool = NewPool();
                await using var held = await pool.OpenReadAsync(source);
                using var cancel = new CancellationTokenSource();
                var path = Path.Combine(directory, "pool-queue-cancel.bin");
                var pending = pool.WriteAsync(path, data, cancellationToken: cancel.Token);
                Check(pool.GetStatistics().PendingAcquires == 1 && !pending.IsCompleted, "Full pool did not queue acquisition.");
                await ThrowsAsync<SmbClientPoolFullException>(() => pool.ReadAsync(source));
                Check(clientCount() == 1 && pool.GetStatistics().RejectedAcquires == 1, "Full queue created an extra client.");
                cancel.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => pending);
                Check(!File.Exists(path) && pool.GetStatistics().PendingAcquires == 0, "Cancelled acquire mutated a file or retained queue capacity.");
                await held.DisposeAsync();
                await pool.ReadAsync(source);
                Check(pool.GetStatistics().ClientsCreated == 1, "Acquire cancellation retired a healthy client.");
            }),
            ("pool acquisition timeout is separate from native I/O", async () =>
            {
                await using var pool = NewPool(acquireTimeout: TimeSpan.FromMilliseconds(60));
                await using var held = await pool.OpenReadAsync(source);
                var path = Path.Combine(directory, "pool-timeout.bin");
                await ThrowsAsync<TimeoutException>(() => pool.WriteAsync(path, data));
                Check(!File.Exists(path) && pool.GetStatistics().AcquireTimeouts == 1 && pool.GetStatistics().PendingAcquires == 0,
                    "Timed-out acquire started I/O or retained queue capacity.");
                await held.DisposeAsync();
                await pool.ReadAsync(source);
            }),
            ("pooled streams retain a lease through EOF and release it exactly once", async () =>
            {
                await using var pool = NewPool();
                var remote = await pool.OpenReadAsync(source);
                using var destination = new MemoryStream();
                await remote.CopyToAsync(destination, 65_539);
                var queued = pool.ReadAsync(source);
                Check(!queued.IsCompleted && pool.GetStatistics().ActiveLeases == 1, "EOF returned an open stream's lease.");
                await remote.DisposeAsync();
                remote.Dispose();
                await remote.DisposeAsync();
                Check((await queued).Content.AsSpan().SequenceEqual(data), "Returned stream client was not reusable.");
                Check(pool.GetStatistics().ActiveLeases == 0 && pool.GetStatistics().ClientsCreated == 1, "Stream returned its lease more than once.");
            }),
            ("pool uses independent clients for concurrent native operations", async () =>
            {
                await using var pool = NewPool(maxClients: 2);
                var firstPath = Path.Combine(directory, "pool-first.blocked");
                var secondPath = Path.Combine(directory, "pool-second.blocked");
                File.WriteAllBytes(firstPath, [1]);
                File.WriteAllBytes(secondPath, [2]);
                await using var first = await pool.OpenReadAsync(firstPath);
                await using var second = await pool.OpenReadAsync(secondPath);
                arm();
                using var watchdog = new Timer(_ => release(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                try
                {
                    var before = slowCount();
                    var firstRead = first.ReadAsync(new byte[8].AsMemory()).AsTask();
                    var secondRead = second.ReadAsync(new byte[8].AsMemory()).AsTask();
                    await WaitUntilAsync(() => slowCount() >= before + 2, "Pooled reads did not start concurrently.");
                    Check(!firstRead.IsCompleted && !secondRead.IsCompleted && clientCount() == 2, "Pool concurrency limit differs.");
                    release();
                    Check(await firstRead == 1 && await secondRead == 1, "Pooled read counts differ.");
                }
                finally { release(); }
            }),
            ("cancelled I/O and stream disposal never return a lease before native completion", async () =>
            {
                await using var pool = NewPool();
                var blockedPath = Path.Combine(directory, "pool-cancel.blocked");
                File.WriteAllBytes(blockedPath, [1, 2, 3]);
                await using var remote = await pool.OpenReadAsync(blockedPath);
                using var cancel = new CancellationTokenSource();
                arm();
                using var watchdog = new Timer(_ => release(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                try
                {
                    var before = slowCount();
                    var read = remote.ReadAsync(new byte[16].AsMemory(), cancel.Token).AsTask();
                    await WaitUntilAsync(() => slowCount() > before, "Blocked pooled I/O did not start.");
                    var queued = pool.ReadAsync(source);
                    cancel.Cancel();
                    var disposal = remote.DisposeAsync().AsTask();
                    await Task.Delay(20);
                    Check(!read.IsCompleted && !disposal.IsCompleted && !queued.IsCompleted && clientCount() == 1,
                        "Cancellation or disposal reused a client still doing native I/O.");
                    release();
                    await ThrowsAsync<OperationCanceledException>(() => read);
                    await disposal;
                    await queued;
                    Check(pool.GetStatistics().ClientsDiscarded == 1 && pool.GetStatistics().ClientsCreated == 2,
                        "Cancelled transfer client was not conservatively retired.");
                }
                finally { release(); }
            }),
            ("pool shutdown rejects waiters but allows an existing stream to finish", async () =>
            {
                var pool = NewPool();
                var remote = await pool.OpenReadAsync(source);
                try
                {
                    var queued = pool.ReadAsync(source);
                    await pool.DisposeAsync();
                    await ThrowsAsync<ObjectDisposedException>(() => queued);
                    await ThrowsAsync<ObjectDisposedException>(() => pool.ReadAsync(source));
                    Check(clientCount() == 1 && transferCount() == 1, "Pool shutdown closed an active stream.");
                    Check(await remote.ReadAsync(new byte[1].AsMemory()) == 1, "Existing stream stopped after pool shutdown.");
                    await remote.DisposeAsync();
                    await pool.DisposeAsync();
                    pool.Dispose();
                    Check(clientCount() == 0 && pool.GetStatistics().ActiveLeases == 0, "Returned lease survived shutdown.");
                }
                finally { await remote.DisposeAsync(); await pool.DisposeAsync(); }
            }),
            ("expected file errors preserve healthy pooled clients", async () =>
            {
                await using var pool = NewPool();
                var missing = await ThrowsAsync<SmbException>(() => pool.ReadAsync(Path.Combine(directory, "pool-missing.bin")));
                Check(missing.ConnectionReusable, "Not-found was not classified as reusable.");
                var collision = await ThrowsAsync<SmbException>(() => pool.WriteAsync(source, data));
                Check(collision.ConnectionReusable, "Already-existing file was not classified as reusable.");
                await pool.ReadAsync(source);
                Check(pool.GetStatistics().ClientsCreated == 1 && pool.GetStatistics().ClientsDiscarded == 0,
                    "Normal file errors discarded the connection.");
            }),
            ("pool discards failed writes and close failures without replay", async () =>
            {
                await using var pool = NewPool();
                var path = Path.Combine(directory, "pool-partial.writefail");
                await ThrowsAsync<SmbException>(() => pool.WriteAsync(path, new byte[] { 1, 2, 3 }));
                Check(new FileInfo(path).Length == 2 && clientCount() == 0 && transferCount() == 0, "Failed write was replayed or leaked.");
                var remote = await pool.OpenWriteAsync(Path.Combine(directory, "pool-close.closefail"));
                await remote.WriteAsync(new byte[] { 42 }.AsMemory());
                await ThrowsAsync<SmbException>(() => remote.DisposeAsync().AsTask());
                await remote.DisposeAsync();
                Check(pool.GetStatistics().ClientsDiscarded == 2 && clientCount() == 0, "Failed stream close returned a client to idle pool.");
                await pool.ReadAsync(source);
            }),
            ("idle expiry closes authenticated clients without new traffic", async () =>
            {
                await using var pool = NewPool(idleTimeout: TimeSpan.FromMilliseconds(60));
                await pool.ReadAsync(source);
                await WaitUntilAsync(() => clientCount() == 0, "Idle client was not closed by the sweeper.");
                Check(pool.GetStatistics().IdleClients == 0 && pool.GetStatistics().ClientsDiscarded == 1, "Idle accounting differs.");
                await pool.ReadAsync(source);
                Check(pool.GetStatistics().ClientsCreated == 2, "Expired client was reused.");
            }),
            ("independent identity pools do not share clients or limits", async () =>
            {
                await using var first = NewPool(maxPending: 0);
                await using var second = PooledSmbClient.Create(ClientOptions("other-user"), new SmbClientPoolOptions { MaxClients = 1 });
                await using var held = await first.OpenReadAsync(source);
                await ThrowsAsync<SmbClientPoolFullException>(() => first.ReadAsync(source));
                await second.ReadAsync(source);
                Check(clientCount() == 2 && first.GetStatistics().ClientsCreated == 1 && second.GetStatistics().ClientsCreated == 1,
                    "Identity pools shared a client or capacity.");
            }),
            ("pool validates limits and pre-cancellation creates no client", async () =>
            {
                await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(PooledSmbClient.Create(ClientOptions(), new SmbClientPoolOptions { MaxClients = 0 })));
                await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(PooledSmbClient.Create(ClientOptions(), new SmbClientPoolOptions { MaxPendingAcquires = -1 })));
                await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(PooledSmbClient.Create(ClientOptions(), new SmbClientPoolOptions { AcquireTimeout = TimeSpan.Zero })));
                await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(PooledSmbClient.Create(ClientOptions(), new SmbClientPoolOptions { IdleTimeout = TimeSpan.Zero })));
                await using var pool = NewPool();
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => pool.ReadAsync(source, cancel.Token));
                Check(clientCount() == 0 && pool.GetStatistics().ClientsCreated == 0, "Pre-cancelled acquire created a client.");
            }),
            ("concurrent pool workload preserves payloads and capacity", async () =>
            {
                await using var pool = NewPool(maxClients: 3, maxPending: 64, acquireTimeout: TimeSpan.FromSeconds(15));
                await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
                {
                    var path = Path.Combine(directory, $"pool-stress-{index}.bin");
                    var payload = data.AsMemory(index, 65_543);
                    await pool.WriteAsync(path, payload);
                    Check((await pool.ReadAsync(path)).Content.AsSpan().SequenceEqual(payload.Span), "Concurrent pooled payload differs.");
                }));
                var stats = pool.GetStatistics();
                Check(stats.ClientsCreated <= 3 && stats.ActiveLeases == 0 && stats.PendingAcquires == 0 && stats.IdleClients <= 3,
                    "Concurrent pool exceeded capacity or leaked a lease.");
            }),
            ("scratch buffer returns once, clears data and hides oversized rental capacity", () =>
            {
                var arrays = new TrackingArrayPool();
                var rented = new PooledBuffer(17, true, arrays);
                Check(rented.Array.Length == 34 && rented.Memory.Length == 17, "Rented capacity leaked into valid count.");
                rented.Memory.Span.Fill(42);
                rented.Dispose();
                rented.Dispose();
                Check(arrays.Returns == 1 && arrays.ClearRequested && arrays.Returned!.AsSpan().IndexOfAnyExcept((byte)0) < 0,
                    "Scratch data was not cleared or was returned twice.");
                return ThrowsAsync<ObjectDisposedException>(() => Task.FromResult(rented.Memory));
            }),
            ("scratch buffer return policy is configurable and finally-safe", async () =>
            {
                var arrays = new TrackingArrayPool();
                await ThrowsAsync<InvalidOperationException>(() =>
                {
                    using var rented = new PooledBuffer(17, false, arrays);
                    throw new InvalidOperationException("fixture source failure");
                });
                Check(arrays.Returns == 1 && !arrays.ClearRequested, "Buffer was lost on failure or clear policy ignored.");
            })
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
                Check(clientCount() == 0 && transferCount() == 0, "Pool check leaked native clients or transfers.");
                Console.WriteLine("PASS " + test.Name);
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + test.Name + Environment.NewLine + error);
            }
            finally { release(); }
        }
        return (tests.Length, failures);
    }

    private static SmbClientOptions ClientOptions(string username = "fixture") => new()
    {
        Server = "localhost", ShareName = "fixture", Username = username, Password = "unused",
        ChunkSizeBytes = 65_539, MaxReadBytes = 16 * 1024 * 1024
    };
    private static PooledSmbClient NewPool(int maxClients = 1, int maxPending = 1,
        TimeSpan? acquireTimeout = null, TimeSpan? idleTimeout = null) =>
        PooledSmbClient.Create(ClientOptions(), new SmbClientPoolOptions
        {
            MaxClients = maxClients, MaxPendingAcquires = maxPending,
            AcquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(2),
            IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(2)
        });
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FixtureAction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FixtureCount();
    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        internal int Returns;
        internal bool ClearRequested;
        internal byte[]? Returned;
        public override byte[] Rent(int minimumLength) => new byte[minimumLength * 2];
        public override void Return(byte[] array, bool clearArray = false)
        {
            Returns++;
            ClearRequested = clearArray;
            Returned = array;
            if (clearArray) array.AsSpan().Clear();
        }
    }
}
