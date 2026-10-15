using System.Runtime.InteropServices;
using StorageGO;

internal static class AsyncChecks
{
    internal static async Task<(int Total, int Failures)> RunAsync(
        string directory, byte[] data, string sourcePath, IntPtr fixture,
        Func<ulong> clientCount, Func<ulong> transferCount)
    {
        var slowCount = Marshal.GetDelegateForFunctionPointer<FixtureCount>(
            NativeLibrary.GetExport(fixture, "StorageGO_FixtureSlowCallCount"));
        var arm = Marshal.GetDelegateForFunctionPointer<FixtureAction>(
            NativeLibrary.GetExport(fixture, "StorageGO_FixtureArmIOGate"));
        var release = Marshal.GetDelegateForFunctionPointer<FixtureAction>(
            NativeLibrary.GetExport(fixture, "StorageGO_FixtureReleaseIOGate"));

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("async byte-array, memory and stream upload round trips", async () =>
            {
                await using var client = NewClient();
                var path = Path.Combine(directory, "async-array.bin");
                var result = await client.WriteAsync(path, data, new SmbWriteOptions { ForceSync = true });
                Check(result.FileSize == data.Length, "Async byte-array write size differs.");
                var read = await client.ReadAsync(path);
                Check(read.Content.AsSpan().SequenceEqual(data), "Async binary array content differs.");
                var memoryPath = Path.Combine(directory, "async-memory.bin");
                result = await client.WriteAsync(memoryPath, data.AsMemory(7, data.Length - 19));
                Check(result.FileSize == data.Length - 19, "Async memory offset size differs.");
                Check(File.ReadAllBytes(memoryPath).AsSpan().SequenceEqual(data.AsSpan(7, data.Length - 19)), "Async memory offset content differs.");
                using var source = new AsyncReadProbe(data);
                var streamPath = Path.Combine(directory, "async-stream.bin");
                await client.WriteAsync(streamPath, source);
                Check(source.AsyncReads > 1 && source.CanRead, "Source stream did not use asynchronous reads or was closed.");
                using var destination = new AsyncWriteProbe();
                result = await client.CopyToAsync(streamPath, destination);
                Check(result.FileSize == data.Length && destination.AsyncWrites > 1 && destination.CanWrite, "Destination did not use async writes or was closed.");
                Check(destination.ToArray().AsSpan().SequenceEqual(data), "Async stream copy differs.");
            }),
            ("async stream array offsets, Memory slices, zero counts and CopyToAsync", async () =>
            {
                await using var client = NewClient();
                var path = Path.Combine(directory, "async-offsets.bin");
                var envelope = new byte[data.Length + 22];
                data.CopyTo(envelope, 7);
                await using (var remote = await client.OpenWriteAsync(path))
                {
                    await remote.WriteAsync(envelope, 7, data.Length / 2);
                    await remote.WriteAsync(data.AsMemory(data.Length / 2));
                    await remote.WriteAsync([], 0, 0);
                    await remote.WriteAsync(ReadOnlyMemory<byte>.Empty);
                    await remote.FlushAsync();
                    await ThrowsAsync<NotSupportedException>(() => remote.ReadAsync(new byte[1], 0, 1));
                }
                await using var input = await client.OpenReadAsync(path);
                Check(await input.ReadAsync([], 0, 0) == 0 && await input.ReadAsync(Memory<byte>.Empty) == 0, "Async zero-count reads differ.");
                await ThrowsAsync<NotSupportedException>(() => input.WriteAsync(new byte[] { 1 }, 0, 1));
                using var received = new MemoryStream();
                var buffer = new byte[150_013];
                for (var iteration = 0; ; iteration++)
                {
                    Array.Fill(buffer, (byte)0xa5);
                    var count = iteration % 2 == 0
                        ? await input.ReadAsync(buffer, 7, buffer.Length - 14)
                        : await input.ReadAsync(buffer.AsMemory(7, buffer.Length - 14));
                    Check(buffer.AsSpan(0, 7).IndexOfAnyExcept((byte)0xa5) < 0, "Async read changed prefix.");
                    Check(buffer.AsSpan(7 + count).IndexOfAnyExcept((byte)0xa5) < 0, "Async read changed suffix.");
                    if (count == 0) break;
                    received.Write(buffer, 7, count);
                }
                Check(received.ToArray().AsSpan().SequenceEqual(data), "Async offset read content differs.");
                await using var copySource = await client.OpenReadAsync(path);
                using var copyDestination = new AsyncWriteProbe();
                await copySource.CopyToAsync(copyDestination, 71_119, CancellationToken.None);
                Check(copyDestination.ToArray().AsSpan().SequenceEqual(data) && copyDestination.AsyncWrites > 1, "Remote Stream.CopyToAsync differs.");
            }),
            ("async empty files, one-byte chunks and in-memory size limits", async () =>
            {
                await using var tinyClient = NewClient(chunkSize: 1);
                var emptyPath = Path.Combine(directory, "async-empty");
                Check((await tinyClient.WriteAsync(emptyPath, Array.Empty<byte>())).FileSize == 0, "Async empty write differs.");
                Check((await tinyClient.ReadAsync(emptyPath)).Content.Length == 0, "Async empty read differs.");
                var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
                var mimeResult = await tinyClient.WriteAsync(Path.Combine(directory, "async-mime"), png);
                Check(mimeResult.MimeType == "image/png", "Async MIME inference failed across one-byte chunks.");
                await using var limited = NewClient(maxReadBytes: 32);
                await ThrowsAsync<SmbException>(() => limited.ReadAsync(sourcePath));
                using var destination = new MemoryStream();
                Check((await limited.CopyToAsync(sourcePath, destination)).FileSize == data.Length, "Async streaming applied in-memory limit.");
            }),
            ("async pre-cancellation, null input and NUL paths avoid file mutation", async () =>
            {
                await using var client = NewClient();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var path = Path.Combine(directory, "async-pre-cancelled.bin");
                File.WriteAllBytes(path, [9, 8, 7]);
                await ThrowsAsync<OperationCanceledException>(() => client.WriteAsync(path, data,
                    new SmbWriteOptions { Overwrite = true }, cancellation.Token));
                await ThrowsAsync<ArgumentNullException>(() => client.WriteAsync(path, (byte[])null!, new SmbWriteOptions { Overwrite = true }));
                await ThrowsAsync<ArgumentException>(() => client.OpenWriteAsync(path + "\0truncated"));
                Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }), "Invalid async operation truncated destination.");
                Check(transferCount() == 0, "Invalid async operation retained a transfer.");
            }),
            ("callback read returns a pending task and cancellation waits for native I/O", async () =>
            {
                await using var client = NewClient();
                var path = Path.Combine(directory, "async-read.blocked");
                File.WriteAllBytes(path, [1, 2, 3]);
                await using var remote = await client.OpenReadAsync(path);
                using var cancellation = new CancellationTokenSource();
                var buffer = new byte[16];
                arm();
                using var watchdog = new Timer(_ => release(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                try
                {
                    var before = slowCount();
                    var pending = remote.ReadAsync(buffer.AsMemory(), cancellation.Token).AsTask();
                    Check(!pending.IsCompleted, "ReadAsync blocked until native I/O completed.");
                    await WaitUntilAsync(() => slowCount() > before, "Native read did not enter its I/O gate.");
                    cancellation.Cancel();
                    await Task.Delay(20);
                    Check(!pending.IsCompleted, "Cancellation released a buffer still owned by native I/O.");
                    release();
                    await ThrowsAsync<OperationCanceledException>(() => pending);
                    Check(buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0, "Cancelled read modified caller memory.");
                    await ThrowsAsync<SmbException>(() => remote.ReadAsync(buffer.AsMemory()).AsTask());
                }
                finally { release(); }
            }),
            ("callback write cancellation reports partial native completion without retry", async () =>
            {
                await using var client = NewClient();
                var path = Path.Combine(directory, "async-write.blocked");
                await using var remote = await client.OpenWriteAsync(path);
                using var cancellation = new CancellationTokenSource();
                arm();
                using var watchdog = new Timer(_ => release(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                try
                {
                    var before = slowCount();
                    var pending = remote.WriteAsync(new byte[] { 7, 8, 9 }.AsMemory(), cancellation.Token).AsTask();
                    Check(!pending.IsCompleted, "WriteAsync blocked its caller on native I/O.");
                    await WaitUntilAsync(() => slowCount() > before, "Native write did not enter its I/O gate.");
                    cancellation.Cancel();
                    await Task.Delay(20);
                    Check(!pending.IsCompleted, "Cancelled write completed before native I/O returned.");
                    release();
                    await ThrowsAsync<OperationCanceledException>(() => pending);
                    await ThrowsAsync<SmbException>(() => remote.WriteAsync(new byte[] { 5 }.AsMemory()).AsTask());
                }
                finally { release(); }
                await remote.DisposeAsync();
                Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 7, 8, 9 }), "Cancelled write was replayed or lost native completion.");
            }),
            ("queued async open cancellation preserves an existing destination", async () =>
            {
                await using var client = NewClient();
                var blockedPath = Path.Combine(directory, "async-open-queue.blocked");
                var destinationPath = Path.Combine(directory, "async-open-existing.bin");
                File.WriteAllBytes(blockedPath, [1]);
                File.WriteAllBytes(destinationPath, [9, 8, 7]);
                await using var remote = await client.OpenReadAsync(blockedPath);
                arm();
                using var watchdog = new Timer(_ => release(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                try
                {
                    var before = slowCount();
                    var read = remote.ReadAsync(new byte[4].AsMemory()).AsTask();
                    await WaitUntilAsync(() => slowCount() > before, "Blocking read did not start.");
                    using var cancellation = new CancellationTokenSource();
                    var open = client.OpenWriteAsync(destinationPath, new SmbWriteOptions { Overwrite = true }, cancellation.Token);
                    cancellation.Cancel();
                    release();
                    await read;
                    await ThrowsAsync<OperationCanceledException>(() => open);
                    Check(File.ReadAllBytes(destinationPath).AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }), "Queued cancelled open truncated an existing file.");
                }
                finally { release(); }
            }),
            ("cancelled in-flight open closes an otherwise unowned transfer", async () =>
            {
                await using var client = NewClient();
                using var cancellation = new CancellationTokenSource();
                var before = slowCount();
                var open = client.OpenWriteAsync(Path.Combine(directory, "async-cancelled-open.slowopen"), cancellationToken: cancellation.Token);
                await WaitUntilAsync(() => slowCount() > before, "Slow open did not start.");
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => open);
                Check(transferCount() == 0, "Cancelled open retained an unowned transfer.");
            }),
            ("independent clients start native reads concurrently", async () =>
            {
                await using var firstClient = NewClient();
                await using var secondClient = NewClient();
                var firstPath = Path.Combine(directory, "async-first.blocked");
                var secondPath = Path.Combine(directory, "async-second.blocked");
                File.WriteAllBytes(firstPath, [1]);
                File.WriteAllBytes(secondPath, [2]);
                await using var first = await firstClient.OpenReadAsync(firstPath);
                await using var second = await secondClient.OpenReadAsync(secondPath);
                arm();
                using var watchdog = new Timer(_ => release(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                try
                {
                    var before = slowCount();
                    var firstRead = first.ReadAsync(new byte[8].AsMemory()).AsTask();
                    var secondRead = second.ReadAsync(new byte[8].AsMemory()).AsTask();
                    await WaitUntilAsync(() => slowCount() >= before + 2, "Independent clients did not reach native I/O concurrently.");
                    Check(!firstRead.IsCompleted && !secondRead.IsCompleted, "Independent read completed while gated.");
                    release();
                    Check(await firstRead == 1 && await secondRead == 1, "Concurrent read byte counts differ.");
                }
                finally { release(); }
            }),
            ("awaited stream disposal retains and then releases a disposed client", async () =>
            {
                var client = NewClient();
                var first = await client.OpenReadAsync(sourcePath);
                var second = await client.OpenReadAsync(sourcePath);
                try
                {
                    await client.DisposeAsync();
                    Check(clientCount() == 1 && transferCount() == 2, "Async client disposal closed active streams.");
                    await ThrowsAsync<ObjectDisposedException>(() => client.OpenReadAsync(sourcePath));
                    Check(await first.ReadAsync(new byte[1].AsMemory()) == 1, "Open stream stopped after client disposal.");
                    await first.DisposeAsync();
                    Check(clientCount() == 1 && transferCount() == 1, "Async disposal released client before final stream.");
                    await second.DisposeAsync();
                    Check(clientCount() == 0 && transferCount() == 0, "Final awaited stream disposal retained native resources.");
                }
                finally
                {
                    await first.DisposeAsync();
                    await second.DisposeAsync();
                    await client.DisposeAsync();
                }
            }),
            ("async native failures and close errors release resources", async () =>
            {
                await using var client = NewClient();
                var writePath = Path.Combine(directory, "async-partial.writefail");
                await using (var remote = await client.OpenWriteAsync(writePath))
                {
                    await ThrowsAsync<SmbException>(() => remote.WriteAsync(new byte[] { 1, 2, 3 }.AsMemory()).AsTask());
                    await ThrowsAsync<SmbException>(() => remote.WriteAsync(new byte[] { 4 }.AsMemory()).AsTask());
                }
                Check(new FileInfo(writePath).Length == 2, "Async short write was replayed.");
                foreach (var suffix in new[] { ".syncfail", ".closefail" })
                {
                    var remote = await client.OpenWriteAsync(Path.Combine(directory, "async-dispose" + suffix), new SmbWriteOptions { ForceSync = true });
                    await remote.WriteAsync(new byte[] { 42 }.AsMemory());
                    await ThrowsAsync<SmbException>(() => remote.DisposeAsync().AsTask());
                    await remote.DisposeAsync();
                    Check(transferCount() == 0, "Failed async close retained transfer.");
                }
                await using (var remote = await client.OpenWriteAsync(Path.Combine(directory, "async-flush.syncfail")))
                    await ThrowsAsync<SmbException>(() => remote.FlushAsync());
            }),
            ("async source and destination failures survive native cleanup failure", async () =>
            {
                await using var client = NewClient();
                var expected = new InvalidOperationException("external async failure");
                using var source = new AsyncReadProbe([], expected);
                var error = await ThrowsAsync<InvalidOperationException>(() => client.WriteAsync(Path.Combine(directory, "async-source-error.closefail"), source));
                Check(ReferenceEquals(error, expected) && source.CanRead, "Async cleanup masked source failure or closed it.");
                using var destination = new AsyncWriteProbe(expected);
                var path = Path.Combine(directory, "async-destination-error.closefail");
                File.WriteAllBytes(path, [1, 2, 3]);
                error = await ThrowsAsync<InvalidOperationException>(() => client.CopyToAsync(path, destination));
                Check(ReferenceEquals(error, expected) && destination.CanWrite, "Async cleanup masked destination failure or closed it.");
            })
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
                Check(clientCount() == 0 && transferCount() == 0, "Async test leaked native clients or transfers.");
                Console.WriteLine("PASS " + test.Name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + test.Name + Environment.NewLine + exception);
            }
            finally { release(); }
        }
        return (tests.Length, failures);
    }

    private static ISmbClient NewClient(int chunkSize = 262_147, long maxReadBytes = 16 * 1024 * 1024) =>
        SmbClient.Create(new SmbClientOptions
        {
            Server = "localhost", ShareName = "fixture", Username = "fixture", Password = "unused",
            ChunkSizeBytes = chunkSize, MaxReadBytes = maxReadBytes
        });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FixtureAction();

    private sealed class AsyncReadProbe(byte[] content, Exception? error = null) : MemoryStream(content, writable: false)
    {
        internal int AsyncReads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            AsyncReads++;
            return error is null ? base.ReadAsync(buffer, cancellationToken) : ValueTask.FromException<int>(error);
        }
    }

    private sealed class AsyncWriteProbe(Exception? error = null) : MemoryStream
    {
        internal int AsyncWrites { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            AsyncWrites++;
            return error is null ? base.WriteAsync(buffer, cancellationToken) : ValueTask.FromException(error);
        }
    }
}
