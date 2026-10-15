# StorageGO

StorageGO, Go ile yazılmış SMB istemcisini .NET 8 uygulamalarında `ISmbClient`
üzerinden kullanıma açar. Windows x64 için `storagego.dll`, Ubuntu/Linux x64 için
`libstoragego.so` kullanılır. Wrapper çalışan platformu algılar ve doğru native
dosyayı otomatik yükler.

## .NET 8 kullanımı

Kimlik bilgilerini kaynak koda yazmak yerine configuration, user-secrets veya bir
secret manager üzerinden alın:

```csharp
using StorageGO;

using ISmbClient client = SmbClient.Create(new SmbClientOptions
{
    Server = configuration["Smb:Server"]!,       // örn. 10.0.0.20
    ShareName = configuration["Smb:ShareName"]!, // örn. documents
    Username = configuration["Smb:Username"]!,
    Password = configuration["Smb:Password"]!,
    Domain = configuration["Smb:Domain"],        // opsiyonel
    DialTimeout = TimeSpan.FromSeconds(15),
    MaxReadBytes = 128 * 1024 * 1024
});

var source = client.Read(@"invoices\2026\invoice.pdf");
await File.WriteAllBytesAsync("invoice.pdf", source.Content);

var content = await File.ReadAllBytesAsync("result.pdf");
var written = client.Write(
    @"outbox\result.pdf",
    content,
    new SmbWriteOptions
    {
        Overwrite = true,
        ForceSync = true,
        MimeType = "application/pdf"
    });
```

`SmbClient.Create` yapılandırmayı doğrular. Ağ bağlantısı ilk `Read` veya `Write`
çağrısında açılır ve client dispose edilene kadar yeniden kullanılır. Her client
kendi bağlantı/session/share yaşam döngüsüne sahiptir. Native hata sonuçları .NET
tarafında `SmbException` olarak döner.

`MaxReadBytes` önemlidir: mevcut ABI bir dosyayı tek çağrıda ve JSON/base64 ile
taşır. Çok büyük dosyalar için ileride stream/chunk tabanlı ayrı bir ABI tercih
edilmelidir.

## Native dosya yerleşimi

NuGet paketi aşağıdaki standart runtime yerleşimini kullanır:

```text
runtimes/
  win-x64/native/storagego.dll
  linux-x64/native/libstoragego.so
```

Wrapper önce bu klasörleri, sonra uygulamanın ana dizinini ve işletim sisteminin
native library arama yollarını kontrol eder. Şu anda yalnızca x64 desteklenir.

## Yerel build

Linux:

```bash
CGO_ENABLED=1 GOOS=linux GOARCH=amd64 \
  go build -trimpath -buildmode=c-shared -o artifacts/linux-x64/libstoragego.so .
```

Windows (MinGW/GCC gerektirir):

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

## GitHub Actions çıktıları

[Build workflow](.github/workflows/build-native.yml) her push, pull request veya
manuel çalıştırmada şu artifact'leri üretir:

- `storagego-linux-x64`: `.so` ve C header
- `storagego-win-x64`: `.dll` ve C header
- `StorageGO-dotnet-package`: her iki native runtime'ı içeren `.nupkg`

Parola yanıt, exception veya log içine eklenmez. `ISmbClient` mutlaka dispose
edilmelidir; `using` kullanımı bunun en güvenli yoludur.
