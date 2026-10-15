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
    ChunkSizeBytes = 1024 * 1024,               // varsayılan: 1 MiB
    MaxReadBytes = 64L * 1024 * 1024             // yalnızca Read için
});

// Büyük dosyaları tamamen belleğe almadan indirme.
using (var destination = File.Create("invoice.pdf"))
{
    SmbWriteResult downloaded = client.CopyTo(
        @"invoices\2026\invoice.pdf", destination);
}

// Büyük dosyaları tamamen belleğe almadan yükleme.
using (var source = File.OpenRead("result.pdf"))
{
    SmbWriteResult uploaded = client.Write(
        @"outbox\result.pdf",
        source,
        new SmbWriteOptions
        {
            Overwrite = true,
            ForceSync = true,
            MimeType = "application/pdf"
        });
}
```

`SmbClient.Create` yapılandırmayı doğrular. Ağ bağlantısı ilk dosya işleminde
açılır ve client dispose edilene kadar yeniden kullanılır. Her client kendi
bağlantı/session/share yaşam döngüsüne sahiptir. Native hata sonuçları .NET
tarafında `SmbException` olarak döner.

Küçük dosyalar için mevcut kolay kullanım korunur; bu metotlar da native katmana
chunk'lar halinde aktarır:

```csharp
SmbFile file = client.Read(@"config\settings.json");
SmbWriteResult result = client.Write(
    @"backup\settings.json", file.Content,
    new SmbWriteOptions { Overwrite = true });
```

`Read` tüm içeriği bir `byte[]` olarak döndürür ve `MaxReadBytes` sınırına tabidir.
Stream metotları bu dosya boyutu sınırına tabi değildir. Büyük dosyalarda
`CopyTo`, `Write(path, Stream)` veya `OpenRead`/`OpenWrite` kullanın.

## Stream ve chunk davranışı

`ISmbClient` şu metotları sunar:

```csharp
SmbFile Read(string remotePath);
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

Stream'i kendiniz yönetmek istediğinizde:

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

Client ve açılan her stream için `using` kullanın. Stream'ler native client'ı
kullanımda tutar; client dispose edildiğinde yeni işlem başlatılamaz, açık
stream'ler kapandığında native client da serbest bırakılır. Normal kullanımda
stream'leri client'tan önce kapatın. `CopyTo` ve `Write(path, Stream)` kendilerine
verilen yerel stream'i kapatmaz. `ForceSync = true`, yazmanın sonunda uzak dosya
için SMB flush ister.

`ChunkSizeBytes` varsayılanı 1 MiB'dır; 1 byte ile 4 MiB arasında ayarlanabilir.
Stream transferinde dosya içeriği JSON/base64'e çevrilmez: her chunk doğrudan C
buffer'ı üzerinden taşınır. JSON yalnızca küçük kontrol/metadata yanıtlarında
kullanılır. Transfer için ek bellek dosya boyutuyla değil chunk boyutuyla büyür;
kullanıcının stream'i ve SMB/.NET çalışma zamanı buffer'ları ayrıca bellek
kullanabilir. `Read` ile dönen `byte[]` ve bellekte oluşturduğunuz kaynak içerik
ise doğal olarak tüm dosyayı bellekte tutar.

İşlemler senkrondur. `CopyTo` ve `Write(path, Stream)` için verilen cancellation
token chunk'lar arasında kontrol edilir; o anda çalışan SMB çağrısını kesmez.
Stream'ler yalnızca ileri yönde ilerler; `Seek`, `Length` ve `Position`
desteklenmez. `ReadAsync`, `WriteAsync`, `FlushAsync` ve `CopyToAsync`
`NotSupportedException` döndürür.

Yazmalar atomik değildir. Hata veya iptal, uzak dosyayı kısmen yazılmış halde
bırakabilir; `Overwrite = true` ile açmak mevcut dosyayı başta kısaltabilir.
Otomatik retry yapılmaz. Atomik yayınlama gerekiyorsa uygulama ayrı bir geçici
dosya ve rename iş akışı tasarlamalıdır.

Bir client'ın native işlemleri sıraya alınır. Eşzamanlı bağımsız transferlerde
ayrı client kullanın; her client kendi SMB oturumunu oluşturur.

## Native ABI v2

.NET wrapper binary chunk ABI v2 gerektirir; wrapper ile native dosyaları birlikte
güncelleyin. Önceki sürümün DLL/SO dosyası yeni wrapper ile kullanılamaz. Eski
`StorageGO_CreateClient`, `StorageGO_Invoke`, `StorageGO_CloseClient` ve
`StorageGO_FreeResult` export'ları eski tüketiciler için korunur; eski JSON/base64
yolu büyük dosyalar için uygun değildir.

Yeni export'lar: `StorageGO_ABIVersion`, `StorageGO_OpenRead`,
`StorageGO_OpenWrite`, `StorageGO_ReadChunk`, `StorageGO_WriteChunk`,
`StorageGO_FlushTransfer` ve `StorageGO_CloseTransfer`. Her build, native
kütüphanenin yanında C ABI imzalarını içeren bir `.h` üretir.

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

## Testler

Go testleri için CGO ve GCC etkin olmalıdır:

```powershell
$env:CGO_ENABLED = "1"
go test -race ./...
```

.NET/native chunk aktarımı testleri için .NET 8, Go, GCC ve PowerShell 7 gerekir:

```powershell
pwsh -File ./tests/run-interop.ps1
```

Bu testler gerçek P/Invoke üzerinden üretimde kullanılan chunk yönetimini yerel
dosyalarla doğrular; SMB sunucusu veya kimlik bilgisi gerektirmez. Test kütüphanesi
ayrı bir dizine üretilir ve yalnızca test uygulamasında yüklenir. Gerçek SMB
sunucusuyla ağ ve kimlik doğrulama entegrasyon testlerinin yerini tutmaz.

## GitHub Actions çıktıları

[Build workflow](.github/workflows/build-native.yml) her push, pull request veya
manuel çalıştırmada şu artifact'leri üretir:

- `storagego-linux-x64`: `.so` ve C header
- `storagego-win-x64`: `.dll` ve C header
- `StorageGO-dotnet-package`: her iki native runtime'ı içeren `.nupkg`

Her iki platformda Go race testleri ve ABI header kontrolü, Windows'ta ayrıca
.NET/native chunk aktarım testleri çalıştırılır.

Windows job'ı `msys2/setup-msys2` ile UCRT64 GCC'yi kurar. Native test/build ve
interop adımları, action'ın bildirdiği kurulum dizininden gelen PATH ve `CC`
ayarlarını kullanır; runner'da önceden kurulu GCC veya sabit bir dizin varsayılmaz.

Parolayı loglamayın veya exception mesajlarına eklemeyin. Kimlik bilgilerini
güvenilir yapılandırmadan okuyun; client ve stream'leri deterministik olarak
dispose edin.
