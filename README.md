**English** | [Türkçe](https://github.com/hakancuraci/StorageGO/blob/main/README.tr.md)

# StorageGO

StorageGO exposes a Go-based SMB client to .NET 8 applications through the
`ISmbClient` interface. It ships `storagego.dll` for Windows x64 and
`libstoragego.so` for Ubuntu/Linux x64. The wrapper detects the current platform
and loads the correct native library automatically.

## Installation

```powershell
dotnet add package StorageGO.Client
```

## Using StorageGO with .NET 8

Read credentials from configuration, user secrets, or a secret manager instead
of storing them in source code:

```csharp
using StorageGO;

await using ISmbClient client = SmbClient.Create(new SmbClientOptions
{
    Server = configuration["Smb:Server"]!,       // e.g. 10.0.0.20
    ShareName = configuration["Smb:ShareName"]!, // e.g. documents
    Username = configuration["Smb:Username"]!,
    Password = configuration["Smb:Password"]!,
    Domain = configuration["Smb:Domain"],         // optional
    DialTimeout = TimeSpan.FromSeconds(15),
    ChunkSizeBytes = 1024 * 1024,                 // default: 1 MiB
    MaxReadBytes = 64L * 1024 * 1024              // Read/ReadAsync only
});

// Download a large file without loading it entirely into memory.
await using (var destination = new FileStream(
    "invoice.pdf", FileMode.Create, FileAccess.Write, FileShare.None,
    bufferSize: 1024 * 1024, useAsync: true))
{
    SmbWriteResult downloaded = await client.CopyToAsync(
        @"invoices\2026\invoice.pdf", destination);
}

// Upload a byte array.
byte[] source = await File.ReadAllBytesAsync("result.pdf");
SmbWriteResult uploaded = await client.WriteAsync(
    @"outbox\result.pdf",
    source,
    new SmbWriteOptions
    {
        Overwrite = true,
        ForceSync = true,
        MimeType = "application/pdf"
    });
```

`SmbClient.Create` validates the configuration. The network connection is opened
on the first file operation and reused until the client is disposed. Each client
owns its connection, session, and share lifecycle. Native failures are exposed as
`SmbException` instances in .NET.

The convenience API remains available for small files. These methods also send
data to the native layer in chunks:

```csharp
SmbFile file = client.Read(@"config\settings.json");
SmbWriteResult result = client.Write(
    @"backup\settings.json", file.Content,
    new SmbWriteOptions { Overwrite = true });
```

`Read` and `ReadAsync` return the entire file as a `byte[]` and are limited by
`MaxReadBytes`. `Write` and `WriteAsync(path, byte[])` retain the source array in
memory, while their additional transfer buffer is bounded by the configured
chunk size. Stream APIs are not subject to the file-size limit. For large files,
use `CopyToAsync`, `WriteAsync(path, Stream)`, `OpenReadAsync`, or
`OpenWriteAsync`. Synchronous equivalents are also available.

## High-throughput APIs: client pooling

`PooledSmbClient` implements the same `ISmbClient` interface. It leases a
dedicated client for each file operation and returns it automatically when the
operation finishes. Application code does not manage `Rent` or `Return` calls.
Operations on one native client are serialized, while separate clients in the
pool can run independent transfers concurrently.

Register the pool as a **singleton** in ASP.NET Core. Do not create a new pool
per request. For example, in `Program.cs`:

```csharp
using StorageGO;

builder.Services.AddSingleton<ISmbClient>(_ => PooledSmbClient.Create(
    new SmbClientOptions
    {
        Server = builder.Configuration["Smb:Server"]!,
        ShareName = builder.Configuration["Smb:ShareName"]!,
        Username = builder.Configuration["Smb:Username"]!,
        Password = builder.Configuration["Smb:Password"]!,
        Domain = builder.Configuration["Smb:Domain"],
        ChunkSizeBytes = 1024 * 1024,
        ClearBuffersOnReturn = true
    },
    new SmbClientPoolOptions
    {
        MaxClients = 8,
        MaxPendingAcquires = 32,
        AcquireTimeout = TimeSpan.FromSeconds(2),
        IdleTimeout = TimeSpan.FromMinutes(2)
    }));
```

Continue using the regular interface inside services:

```csharp
public sealed class DocumentStorage(ISmbClient client)
{
    public Task<SmbWriteResult> SaveAsync(
        string path, byte[] source, CancellationToken cancellationToken) =>
        client.WriteAsync(path, source,
            new SmbWriteOptions { Overwrite = true }, cancellationToken);

    public Task<SmbWriteResult> DownloadAsync(
        string path, Stream destination, CancellationToken cancellationToken) =>
        client.CopyToAsync(path, destination, cancellationToken);
}
```

Do not dispose the injected singleton in request or service methods; let the DI
container manage its lifecycle. You must still dispose every stream returned by
`OpenReadAsync` or `OpenWriteAsync`:

```csharp
await using Stream remote = await client.OpenReadAsync(path, cancellationToken);
await remote.CopyToAsync(destination, cancellationToken);
```

A stream retains its client lease until it is closed, even after reaching EOF.
Consequently, `MaxClients` also limits concurrently open file transfers. Account
for this when one operation waits for another client while a stream remains open;
in particular, do not wait for a second operation with `MaxClients = 1` before
closing the first stream.

The values above are defaults and a starting point for load testing:

- Clients are created on demand. TCP connection and authentication happen on the
  first file operation; constructing the pool does not pre-warm connections.
- When capacity is full, up to `MaxPendingAcquires` requests wait. If the queue is
  also full, `SmbClientPoolFullException` is thrown. Set
  `MaxPendingAcquires = 0` to disable waiting.
- A client acquisition timeout produces `TimeoutException`; cancellation while
  waiting produces `OperationCanceledException`. No file operation starts in
  either case. `AcquireTimeout` is not an active SMB I/O timeout.
- Idle clients are closed by periodic cleanup after `IdleTimeout`. Expected file
  errors such as not found, already exists, and access denied preserve the
  connection. Unknown native failures, canceled transfers, or close failures
  evict the client conservatively. Failed writes are not retried automatically.
- Closing the pool rejects new work and closes idle clients. `DisposeAsync` does
  not forcibly close or wait for streams retained by the application; their
  clients are closed when those streams finish.
- Each pool belongs to one server, share, and identity. Use separate pools for
  different users or tenants. For password rotation, create a new pool and close
  the old one. Never put passwords in pool keys or logs.
- Limits apply per process and per pool. Adding API replicas or identity pools
  increases the total number of connections to the NAS.

`PooledSmbClient.GetStatistics()` returns a snapshot of active leases, pending
requests, idle clients, created and evicted clients, timeouts, queue rejections,
and cleanup failures. For a concrete pool instance, use
`var stats = pool.GetStatistics();`. Connection failures are classified using
the native `ConnectionReusable` value instead of matching message text. If an
older ABI v3 library omits this value, the failed client is conservatively
removed from the pool.

## Buffer pools

On the .NET side, stream and helper transfer buffers are rented from
`ArrayPool<byte>` and reused throughout a transfer, avoiding a new large array
for every chunk. Only the requested chunk or count is used even when the rented
array is larger. `ClearBuffersOnReturn` defaults to `true`, clearing internal
managed arrays before returning them. Disabling it can reduce clearing cost but
may leave sensitive data in the shared pool.

The Go async layer uses buffer classes of 64 KiB, 256 KiB, 1 MiB, and 4 MiB. It
retains at most 16 buffers per class and at most **32 MiB of idle native buffers**
in total. Returned buffers are cleared across their full capacity. Read callback
data remains valid until the callback returns, and an owned write copy remains
valid until its operation completes. Cancellation does not release the client or
buffer prematurely.

The 32 MiB value is **not a total process memory limit**. Buffers in use, the .NET
shared array pool, SMB/runtime buffers, other pools, and application-owned file
arrays consume additional memory. The client pool limits concurrent transfers;
monitor .NET and Go memory together. `ReadAsync` still retains the entire result
in memory, so prefer streaming for large files.

Application-provided `byte[]` values and the `Content` arrays returned by
`Read`/`ReadAsync` are never returned to internal pools. Do not mutate or return a
source array to your own pool before `WriteAsync` completes. Buffer pooling lowers
allocation and GC pressure; it does not automatically eliminate all copies or
guarantee a specific network throughput improvement.

## Stream and chunk behavior

`ISmbClient` supports both `IDisposable` and `IAsyncDisposable`. Async methods
accept cancellation tokens:

```csharp
Task<SmbFile> ReadAsync(string remotePath,
    CancellationToken cancellationToken = default);
Task<SmbWriteResult> WriteAsync(string remotePath, byte[] source,
    SmbWriteOptions? options = null,
    CancellationToken cancellationToken = default);
Task<SmbWriteResult> WriteAsync(string remotePath, ReadOnlyMemory<byte> content,
    SmbWriteOptions? options = null,
    CancellationToken cancellationToken = default);
Task<SmbWriteResult> WriteAsync(string remotePath, Stream source,
    SmbWriteOptions? options = null,
    CancellationToken cancellationToken = default);
Task<Stream> OpenReadAsync(string remotePath,
    CancellationToken cancellationToken = default);
Task<Stream> OpenWriteAsync(string remotePath, SmbWriteOptions? options = null,
    CancellationToken cancellationToken = default);
Task<SmbWriteResult> CopyToAsync(string remotePath, Stream destination,
    CancellationToken cancellationToken = default);
```

The existing synchronous methods remain available:

```csharp
SmbFile Read(string remotePath);
SmbWriteResult Write(string remotePath, byte[] source,
    SmbWriteOptions? options = null);
SmbWriteResult Write(string remotePath, ReadOnlyMemory<byte> content,
    SmbWriteOptions? options = null);
Stream OpenRead(string remotePath);
Stream OpenWrite(string remotePath, SmbWriteOptions? options = null);
SmbWriteResult CopyTo(string remotePath, Stream destination,
    CancellationToken cancellationToken = default);
SmbWriteResult Write(string remotePath, Stream source,
    SmbWriteOptions? options = null,
    CancellationToken cancellationToken = default);
```

For direct stream management:

```csharp
await using (Stream remote = await client.OpenReadAsync(@"archives\large.zip"))
await using (var local = new FileStream(
    "large.zip", FileMode.Create, FileAccess.Write, FileShare.None,
    bufferSize: 1024 * 1024, useAsync: true))
{
    await remote.CopyToAsync(local, bufferSize: 1024 * 1024);
}

await using (var local = new FileStream(
    "large.zip", FileMode.Open, FileAccess.Read, FileShare.Read,
    bufferSize: 1024 * 1024, useAsync: true))
await using (Stream remote = await client.OpenWriteAsync(
    @"archives\large.zip", new SmbWriteOptions { Overwrite = true }))
{
    await local.CopyToAsync(remote, bufferSize: 1024 * 1024);
}
```

Synchronous usage:

```csharp
using (Stream remote = client.OpenRead(@"archives\large.zip"))
using (FileStream local = File.Create("large.zip"))
{
    remote.CopyTo(local, bufferSize: 1024 * 1024);
}

using (FileStream local = File.OpenRead("large.zip"))
using (Stream remote = client.OpenWrite(
    @"archives\large.zip", new SmbWriteOptions { Overwrite = true }))
{
    local.CopyTo(remote, bufferSize: 1024 * 1024);
}
```

Use `await using` for each client and stream in asynchronous code, and `using` in
synchronous code. Streams retain the native client. Disposing a client prevents
new operations; its native resources are released after its open streams close.
Normally, close streams before their client. `CopyTo`/`CopyToAsync` and
`Write`/`WriteAsync(path, Stream)` do not close the local stream passed by the
caller. `Flush`/`FlushAsync` requests an SMB flush, and `ForceSync = true` also
flushes after a successful write.

`ChunkSizeBytes` defaults to 1 MiB and can be configured from 1 byte to 4 MiB.
Stream contents are never encoded as JSON/base64; each chunk passes directly
through a C buffer. JSON is limited to small control and metadata responses.
Additional transfer memory grows with chunk size rather than file size, excluding
the caller's stream and SMB/.NET runtime buffers. A `byte[]` returned by `Read`,
or in-memory source data, naturally retains the full file.

Streams are forward-only; `Seek`, `Length`, and `Position` are unsupported.
`ReadAsync`, `WriteAsync`, `FlushAsync`, `CopyToAsync`, and `DisposeAsync` are
supported. Async operations complete through Go goroutines and completion
callbacks; .NET does not hold a `Task.Run` worker thread while waiting for SMB
I/O. The local stream must also support asynchronous I/O; the `FileStream`
examples above use `useAsync: true`.

A cancellation token can prevent a pending operation from starting and is
checked between chunks. The underlying SMB library does not accept a separate
cancellation context for an active `Read` or `Write`, so in-flight I/O is not
interrupted immediately. Because buffers remain in native use, a canceled await
completes only after the active native call finishes. Cancellation does not roll
back previously written data. Synchronous stream operations also check
cancellation between calls.

Writes are not atomic. Failure or cancellation can leave a partially written
remote file, and opening with `Overwrite = true` can truncate an existing file
immediately. No automatic retry is performed. If atomic publication is required,
implement a temporary-file-and-rename workflow in the application.

Native operations on one client are serialized; async APIs do not provide
parallel SMB chunk I/O on that same client. Their primary benefit is releasing
.NET threads while network I/O waits, not guaranteeing higher throughput for one
file. Use a bounded number of separate clients for concurrent independent
transfers. Each client creates its own SMB session. Bound application concurrency
or use the client pool. The process-wide native async admission limit is 1,024
operations; new work is rejected at the limit instead of building an unbounded
queue.

## Native ABI v3

The .NET wrapper 1.2.x requires async binary chunk ABI v3. Update the wrapper and
native libraries together. ABI v1/v2 DLL or SO files are incompatible with the
new wrapper. Legacy `StorageGO_CreateClient`, `StorageGO_Invoke`,
`StorageGO_CloseClient`, and `StorageGO_FreeResult` exports remain for older
consumers, although the legacy JSON/base64 path is unsuitable for large files.

Synchronous chunk exports remain available: `StorageGO_ABIVersion`,
`StorageGO_OpenRead`, `StorageGO_OpenWrite`, `StorageGO_ReadChunk`,
`StorageGO_WriteChunk`, `StorageGO_FlushTransfer`, and
`StorageGO_CloseTransfer`. Each build emits a `.h` file containing the C ABI
signatures next to the native library.

The async ABI adds `StorageGO_BeginAsync` and `StorageGO_CancelAsync`.
`BeginAsync` returns a non-zero operation ID when accepted and `0` when rejected.
Payloads use binary buffers; write input is copied into native-owned memory before
`BeginAsync` returns. Completion results and read buffers are valid only for the
duration of the callback; consumers must copy data before returning and must not
free these pointers. The .NET wrapper manages data and callback-context ownership.
`StorageGO_ReleaseClient` and `StorageGO_ReleaseTransfer` initiate non-blocking
native cleanup for SafeHandle/finalizer paths and are not subject to the async
admission limit.

## Native library layout

The NuGet package uses the standard runtime layout:

```text
runtimes/
  win-x64/native/storagego.dll
  linux-x64/native/libstoragego.so
```

The wrapper checks these directories first, followed by the application base
directory and the operating system's native library search paths. Only x64 is
currently supported.

## Local build

Linux:

```bash
CGO_ENABLED=1 GOOS=linux GOARCH=amd64 \
  go build -trimpath -buildmode=c-shared -o artifacts/linux-x64/libstoragego.so .
```

Windows (requires MinGW/GCC):

```powershell
$env:CGO_ENABLED = "1"
$env:GOOS = "windows"
$env:GOARCH = "amd64"
go build -trimpath -buildmode=c-shared -o artifacts/win-x64/storagego.dll .
```

.NET wrapper:

```powershell
dotnet build StorageGO.sln -c Release
```

## Tests

CGO and GCC must be enabled for the Go tests:

```powershell
$env:CGO_ENABLED = "1"
go test -race ./...
```

The .NET/native synchronous and asynchronous chunk-transfer tests require .NET
8, Go, GCC, and PowerShell 7:

```powershell
pwsh -File ./tests/run-interop.ps1
```

Client-pool tests cover capacity and queue limits, timeouts and cancellation,
stream lease lifetime, client eviction after failures or idle timeout, and
concurrent payload integrity. Buffer tests cover return, clearing, sizing, and
callback ownership rules. These are not real NAS performance benchmarks.

The tests exercise production chunk management through real P/Invoke and
completion callbacks against local files, without requiring an SMB server or
credentials. The fixture library is built into a separate directory and loaded
only by the test application. These tests do not replace network and
authentication integration testing against a real SMB server.

Run the Go buffer-pool allocation micro-benchmark separately. It does not measure
NAS or network throughput:

```powershell
go test -run '^$' -bench BenchmarkNativeBufferAllocation -benchmem
```

## GitHub Actions artifacts

The [build workflow](https://github.com/hakancuraci/StorageGO/blob/main/.github/workflows/build-native.yml)
runs for every push, pull request, or manual invocation and produces:

- `storagego-linux-x64`: `.so` and C header
- `storagego-win-x64`: `.dll` and C header
- `StorageGO-dotnet-package`: `.nupkg` containing both native runtimes

Both platforms run Go race tests, ABI header verification, and .NET/native
synchronous and asynchronous chunk-transfer tests.

### Automatic publishing to NuGet.org

Publishing uses NuGet.org Trusted Publishing and GitHub Actions OIDC; no
long-lived NuGet API key or GitHub repository secret is required. The active
NuGet.org policy authorizes the `build-native.yml` workflow in
`hakancuraci/StorageGO` to publish `StorageGO.Client`. The workflow obtains a
temporary publishing key through `NuGet/login` only during the publishing job.

Create and push a version tag to publish a release:

```powershell
git tag v1.2.2
git push origin v1.2.2
```

The workflow rebuilds and tests the Windows and Linux native libraries, removes
the leading `v` to derive package version `1.2.2`, and publishes the package to
NuGet.org. Prerelease tags such as `v1.3.0-beta.1` are supported. If the version
already exists, `--skip-duplicate` lets the step finish without an error. NuGet
package versions are immutable, so always choose a new version instead of reusing
a published tag.

The Windows job installs UCRT64 GCC with `msys2/setup-msys2`. Native test/build
and interop steps use the PATH and `CC` values from the action's reported install
directory; they do not assume a preinstalled compiler or a fixed runner path.

Never log passwords or include them in exception messages. Load credentials from
trusted configuration and dispose clients and streams deterministically.
