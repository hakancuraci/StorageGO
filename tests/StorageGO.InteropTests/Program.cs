using System.Runtime.InteropServices;
using StorageGO;

if (args.Contains("--production-smoke"))
{
    var client = NewClient();
    Throws<ArgumentException>(() => client.OpenRead("file\0truncated.txt"));
    client.Dispose();
    Throws<ObjectDisposedException>(() => client.OpenRead("file.txt"));
    Console.WriteLine("PASS production ABI version, client create/dispose and managed path validation");
    return 0;
}

var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
var libraryName = OperatingSystem.IsWindows() ? "storagego.dll" : "libstoragego.so";
var fixturePath = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", libraryName);
var fixture = NativeLibrary.Load(fixturePath);
var marker = Marshal.GetDelegateForFunctionPointer<FixtureMarker>(
    NativeLibrary.GetExport(fixture, "StorageGO_FixtureIsLocalFile"));
Check(marker() == 1, "The isolated local-file fixture must be loaded.");
var clientCount = Marshal.GetDelegateForFunctionPointer<FixtureCount>(
    NativeLibrary.GetExport(fixture, "StorageGO_FixtureClientCount"));
var transferCount = Marshal.GetDelegateForFunctionPointer<FixtureCount>(
    NativeLibrary.GetExport(fixture, "StorageGO_FixtureTransferCount"));

var directory = Path.Combine(Path.GetTempPath(), "storagego-interop-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var data = new byte[8 * 1024 * 1024 + 113];
new Random(1741).NextBytes(data);
var sourcePath = Path.Combine(directory, "binary-source.bin");
File.WriteAllBytes(sourcePath, data);

var tests = new (string Name, Action Run)[]
{
    ("byte-array round trip uses chunk ABI and preserves memory overload", () =>
    {
        using var client = NewClient();
        var path = Path.Combine(directory, "Türkçe-çığ-📁.bin");
        var written = client.Write(remotePath: path, source: data, options: new SmbWriteOptions { ForceSync = true });
        Check(written.FileSize == data.Length && written.FileName == Path.GetFileName(path), "Write metadata differs.");
        var read = client.Read(path);
        Check(read.FileSize == data.Length && read.Content.AsSpan().SequenceEqual(data), "Binary round trip differs.");
        Throws<SmbException>(() => client.Write(path, new byte[] { 1 }.AsMemory()));
        client.Write(path, new byte[] { 4, 5 }.AsMemory(), new SmbWriteOptions { Overwrite = true });
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 4, 5 }), "Overwrite did not truncate.");
    }),
    ("byte-array null is rejected before overwrite and empty array creates an empty file", () =>
    {
        using var client = NewClient();
        var path = Path.Combine(directory, "byte-array-null.bin");
        File.WriteAllBytes(path, [9, 8, 7]);
        byte[] source = null!;
        var error = Throws<ArgumentNullException>(() => client.Write(path, source,
            new SmbWriteOptions { Overwrite = true }));
        Check(error.ParamName == "source", "Null validation did not identify source.");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }), "Null array truncated the destination.");
        Check(transferCount() == 0, "Null array opened a transfer.");
        var result = client.Write(path, Array.Empty<byte>(), new SmbWriteOptions { Overwrite = true });
        Check(result.FileSize == 0 && new FileInfo(path).Length == 0, "Empty array did not create an empty file.");
    }),
    ("array offsets, spans, zero counts and forward-only modes", () =>
    {
        using var client = NewClient();
        var path = Path.Combine(directory, "offsets.bin");
        var envelope = new byte[data.Length + 22];
        data.CopyTo(envelope, 7);
        using (var output = client.OpenWrite(path))
        {
            Check(output.CanWrite && !output.CanRead && !output.CanSeek, "Write capabilities differ.");
            output.Write(envelope, 7, data.Length / 2);
            output.Write(data.AsSpan(data.Length / 2));
            output.Write([], 0, 0);
            output.Write(ReadOnlySpan<byte>.Empty);
            Throws<NotSupportedException>(() => output.Read(new byte[1], 0, 1));
            output.Flush();
        }
        using var input = client.OpenRead(path);
        Check(input.CanRead && !input.CanWrite && !input.CanSeek, "Read capabilities differ.");
        Check(input.Read([], 0, 0) == 0 && input.Read(Span<byte>.Empty) == 0, "Empty read was not empty.");
        Throws<NotSupportedException>(() => input.Write(new byte[] { 1 }, 0, 1));
        Throws<NotSupportedException>(() => _ = input.Length);
        Throws<NotSupportedException>(() => input.Seek(0, SeekOrigin.Begin));
        Throws<NotSupportedException>(() => input.ReadAsync(new byte[1], 0, 1).GetAwaiter().GetResult());
        using var received = new MemoryStream();
        var buffer = new byte[150_013];
        for (var iteration = 0; ; iteration++)
        {
            Array.Fill(buffer, (byte)0xa5);
            var count = iteration % 2 == 0
                ? input.Read(buffer, 7, buffer.Length - 14)
                : input.Read(buffer.AsSpan(7, buffer.Length - 14));
            Check(buffer.AsSpan(0, 7).IndexOfAnyExcept((byte)0xa5) < 0, "Read changed buffer prefix.");
            Check(buffer.AsSpan(7 + count).IndexOfAnyExcept((byte)0xa5) < 0, "Read changed buffer suffix.");
            if (count == 0) break;
            received.Write(buffer, 7, count);
        }
        Check(received.ToArray().AsSpan().SequenceEqual(data), "Array/Span content differs.");
        Check(input.Read(new byte[1], 0, 1) == 0, "Read after EOF was not empty.");
        input.Flush();
    }),
    ("stream copy bypasses in-memory size limit and leaves external streams open", () =>
    {
        using var client = NewClient(maxReadBytes: 32);
        Throws<SmbException>(() => client.Read(sourcePath));
        using var destination = new MemoryStream();
        var copied = client.CopyTo(sourcePath, destination);
        Check(copied.FileSize == data.Length && destination.CanWrite, "Copy metadata or stream ownership differs.");
        Check(destination.ToArray().AsSpan().SequenceEqual(data), "Copy content differs.");
        using var source = new MemoryStream(data, writable: false);
        var path = Path.Combine(directory, "source-stream.bin");
        var written = client.Write(path, source);
        Check(written.FileSize == data.Length && source.CanRead, "Write metadata or stream ownership differs.");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "Stream write differs.");
    }),
    ("empty files and minimum/maximum chunk sizes", () =>
    {
        foreach (var chunkSize in new[] { 1, 4 * 1024 * 1024 })
        {
            using var client = NewClient(chunkSize: chunkSize);
            var path = Path.Combine(directory, "empty-" + chunkSize);
            using var source = new MemoryStream();
            Check(client.Write(path, source).FileSize == 0, "Empty source wrote bytes.");
            Check(client.Read(path).Content.Length == 0, "Empty file read bytes.");
            using var remote = client.OpenRead(path);
            Check(remote.Read(new byte[3], 0, 3) == 0, "Empty file did not return EOF.");
            var tinyPath = path + ".tiny";
            client.Write(tinyPath, new byte[] { 0, 128, 255 }.AsMemory());
            Check(client.Read(tinyPath).Content.AsSpan().SequenceEqual(new byte[] { 0, 128, 255 }), "Boundary chunk content differs.");
        }
    }),
    ("unknown-extension upload MIME inference survives one-byte chunks", () =>
    {
        using var client = NewClient(chunkSize: 1);
        foreach (var sample in new (string Name, byte[] Data, string Mime)[]
        {
            ("png", [137, 80, 78, 71, 13, 10, 26, 10, 0], "image/png"),
            ("pdf", [37, 80, 68, 70, 45, 49], "application/pdf"),
            ("jpeg", [255, 216, 255, 224], "image/jpeg")
        })
        {
            var memoryResult = client.Write(Path.Combine(directory, "mime-memory-" + sample.Name), sample.Data.AsMemory());
            Check(memoryResult.MimeType == sample.Mime, "Memory upload MIME differs for " + sample.Name);
            using var source = new MemoryStream(sample.Data, writable: false);
            var streamResult = client.Write(Path.Combine(directory, "mime-stream-" + sample.Name), source);
            Check(streamResult.MimeType == sample.Mime, "Stream upload MIME differs for " + sample.Name);
            var explicitResult = client.Write(Path.Combine(directory, "mime-explicit-" + sample.Name), sample.Data.AsMemory(),
                new SmbWriteOptions { MimeType = "application/octet-stream" });
            Check(explicitResult.MimeType == "application/octet-stream", "Explicit MIME was overwritten.");
        }
    }),
    ("open streams retain client until the final stream is disposed", () =>
    {
        var client = NewClient();
        var first = client.OpenRead(sourcePath);
        var second = client.OpenRead(sourcePath);
        try
        {
            client.Dispose();
            Check(clientCount() == 1 && transferCount() == 2, "Disposing client released active streams.");
            Throws<ObjectDisposedException>(() => client.OpenRead(sourcePath));
            Check(first.ReadByte() == data[0] && second.ReadByte() == data[0], "Active stream stopped after client disposal.");
            first.Dispose();
            Check(clientCount() == 1 && transferCount() == 1, "Client released before final stream.");
            second.Dispose();
            Check(clientCount() == 0 && transferCount() == 0, "Final stream retained client/transfer.");
            Throws<ObjectDisposedException>(() => first.ReadByte());
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            client.Dispose();
        }
    }),
    ("cancellation before opening preserves an existing destination", () =>
    {
        using var client = NewClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = Path.Combine(directory, "pre-cancelled.bin");
        File.WriteAllBytes(path, [9, 8, 7]);
        using var source = new MemoryStream(data, writable: false);
        Throws<OperationCanceledException>(() => client.Write(path, source, new SmbWriteOptions { Overwrite = true }, cancellation.Token));
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 9, 8, 7 }), "Pre-cancellation truncated destination.");
        using var destination = new MemoryStream();
        Throws<OperationCanceledException>(() => client.CopyTo(sourcePath, destination, cancellation.Token));
        Check(destination.Length == 0 && transferCount() == 0, "Pre-cancellation opened a transfer.");
    }),
    ("cancellation between chunks stops transfer without replay", () =>
    {
        using var client = NewClient();
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelAfterReadStream(data, cancellation);
        var path = Path.Combine(directory, "partial-cancelled.bin");
        Throws<OperationCanceledException>(() => client.Write(path, source, cancellationToken: cancellation.Token));
        Check(source.CanRead && new FileInfo(path).Length < data.Length, "Cancellation did not stop source transfer.");
        Check(source.ReadCalls == 2 && transferCount() == 0, "Cancellation replayed source or leaked transfer.");
    }),
    ("partial native failure is surfaced and the failed stream cannot replay", () =>
    {
        using var client = NewClient();
        var path = Path.Combine(directory, "partial.writefail");
        using (var remote = client.OpenWrite(path, new SmbWriteOptions { ForceSync = true }))
        {
            Throws<SmbException>(() => remote.Write(new byte[] { 1, 2, 3, 4 }, 0, 4));
            Throws<SmbException>(() => remote.Write(new byte[] { 5 }, 0, 1));
        }
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 1, 2 }), "Failed native write was replayed.");
    }),
    ("flush, force-sync and close failures release transfers", () =>
    {
        using var client = NewClient();
        foreach (var suffix in new[] { ".syncfail", ".closefail" })
        {
            var remote = client.OpenWrite(Path.Combine(directory, "dispose" + suffix), new SmbWriteOptions { ForceSync = true });
            remote.WriteByte(42);
            Throws<SmbException>(remote.Dispose);
            remote.Dispose();
            Check(transferCount() == 0, "Failed close retained transfer.");
        }
        using (var remote = client.OpenWrite(Path.Combine(directory, "flush.syncfail")))
        {
            Throws<SmbException>(remote.Flush);
            Throws<SmbException>(() => remote.WriteByte(1));
        }
        Check(transferCount() == 0, "Failed flush retained transfer.");
    }),
    ("external stream errors survive a simultaneous native close error", () =>
    {
        using var client = NewClient();
        var expected = new InvalidOperationException("external source failure");
        using var source = new ThrowingReadStream(expected);
        var error = Throws<InvalidOperationException>(() => client.Write(Path.Combine(directory, "source-error.closefail"), source));
        Check(ReferenceEquals(error, expected) && source.CanRead, "Cleanup replaced the source exception or disposed source.");
        using var destination = new ThrowingWriteStream(expected);
        var path = Path.Combine(directory, "destination-error.closefail");
        File.WriteAllBytes(path, [1, 2, 3]);
        error = Throws<InvalidOperationException>(() => client.CopyTo(path, destination));
        Check(ReferenceEquals(error, expected) && destination.CanWrite, "Cleanup replaced the destination exception or disposed destination.");
    }),
    ("invalid options and paths fail before native file operations", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => NewClient(chunkSize: 0));
        Throws<ArgumentOutOfRangeException>(() => NewClient(chunkSize: 4 * 1024 * 1024 + 1));
        using var client = NewClient();
        Throws<ArgumentException>(() => client.OpenRead(" \t "));
        Throws<ArgumentException>(() => client.OpenWrite(sourcePath + "\0ignored"));
        Check(transferCount() == 0, "Invalid path opened a transfer.");
    })
};

var failures = 0;
try
{
    foreach (var test in tests)
    {
        try
        {
            test.Run();
            Check(clientCount() == 0 && transferCount() == 0, "Test leaked native clients or transfers.");
            Console.WriteLine("PASS " + test.Name);
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine("FAIL " + test.Name + Environment.NewLine + exception);
        }
    }
}
finally
{
    Directory.Delete(directory, recursive: true);
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} interop checks passed.");
return failures == 0 ? 0 : 1;

static ISmbClient NewClient(int chunkSize = 65_539, long maxReadBytes = 16 * 1024 * 1024) =>
    SmbClient.Create(new SmbClientOptions
    {
        Server = "localhost", ShareName = "fixture", Username = "fixture", Password = "unused",
        ChunkSizeBytes = chunkSize, MaxReadBytes = maxReadBytes
    });

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T exception) { return exception; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate uint FixtureMarker();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate ulong FixtureCount();

sealed class CancelAfterReadStream(byte[] data, CancellationTokenSource cancellation) : MemoryStream(data, writable: false)
{
    public int ReadCalls { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = base.Read(buffer, offset, count);
        if (++ReadCalls == 2) cancellation.Cancel();
        return read;
    }
}

sealed class ThrowingReadStream(Exception error) : MemoryStream
{
    public override int Read(byte[] buffer, int offset, int count) => throw error;
}

sealed class ThrowingWriteStream(Exception error) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count) => throw error;
}
