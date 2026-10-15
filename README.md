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

await using ISmbClient client = SmbClient.Create(new SmbClientOptions
{
    Server = configuration["Smb:Server"]!,       // örn. 10.0.0.20
    ShareName = configuration["Smb:ShareName"]!, // örn. documents
    Username = configuration["Smb:Username"]!,
    Password = configuration["Smb:Password"]!,
    Domain = configuration["Smb:Domain"],        // opsiyonel
    DialTimeout = TimeSpan.FromSeconds(15),
    ChunkSizeBytes = 1024 * 1024,               // varsayılan: 1 MiB
    MaxReadBytes = 64L * 1024 * 1024             // yalnızca Read/ReadAsync için
});

// Büyük dosyaları tamamen belleğe almadan indirme.
await using (var destination = new FileStream(
    "invoice.pdf", FileMode.Create, FileAccess.Write, FileShare.None,
    bufferSize: 1024 * 1024, useAsync: true))
{
    SmbWriteResult downloaded = await client.CopyToAsync(
        @"invoices\2026\invoice.pdf", destination);
}

// byte[] kaynağını yükleme.
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

`Read` ve `ReadAsync` tüm içeriği bir `byte[]` olarak döndürür ve `MaxReadBytes`
sınırına tabidir. `Write`/`WriteAsync(path, byte[])` kaynak diziyi tamamen bellekte
tutar; native katmana aktarım binary chunk'larla yapılır ve ek transfer buffer'ı
chunk boyutuyla sınırlıdır. Stream metotları bu dosya boyutu sınırına tabi
değildir. Büyük dosyalarda `CopyToAsync`, `WriteAsync(path, Stream)` veya
`OpenReadAsync`/`OpenWriteAsync` kullanın; senkron karşılıkları da korunur.

## Yoğun API kullanımı: client havuzu

`PooledSmbClient`, aynı `ISmbClient` arayüzünü uygular. Her dosya işlemi için
havuzdan özel bir client alır ve işlem tamamlandığında geri verir. Kullanıcı
kodunda `Rent`/`Return` yönetimi gerekmez. Tek native client'ın işlemleri
sıralanırken, havuzdaki farklı client'lar bağımsız transferleri eşzamanlı yürütür.

ASP.NET Core'da **havuzu singleton** kaydedin; request başına yeni havuz
oluşturmayın. Örneğin `Program.cs` içinde:

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

Servis içinde normal arayüzü kullanmaya devam edin:

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

Enjekte edilen singleton client'ı request/servis metotlarında dispose etmeyin;
yaşam döngüsünü uygulamanın DI container'ı yönetir. `OpenReadAsync` veya
`OpenWriteAsync` ile aldığınız **stream'i ise mutlaka dispose edin**:

```csharp
await using Stream remote = await client.OpenReadAsync(path, cancellationToken);
await remote.CopyToAsync(destination, cancellationToken);
```

Stream EOF'a ulaşsa bile kapanana kadar bir client'ı kirada tutar. Dolayısıyla
`MaxClients`, aynı anda açık dosya transferlerini de sınırlar. Bir stream açıkken
aynı havuzdan başka bir işlem bekletiyorsanız kapasite ihtiyacını hesaba katın;
özellikle `MaxClients = 1` ile açık stream'i kapatmadan ikinci işlemi beklemeyin.

Yukarıdaki değerler varsayılanlardır ve yük testi için başlangıç noktasıdır:

- Client'lar ihtiyaç oldukça oluşturulur. TCP bağlantısı ve kimlik doğrulama ilk
  dosya işleminde yapılır; havuz oluşturmak bağlantıları önceden ısıtmaz.
- Kapasite dolunca en fazla `MaxPendingAcquires` istek bekler. Kuyruk da doluysa
  `SmbClientPoolFullException` fırlatılır; `MaxPendingAcquires = 0` beklemeyi kapatır.
- Client bekleme süresi dolarsa `TimeoutException`, bekleme iptal edilirse
  `OperationCanceledException` döner. Bu durumlarda dosya işlemi başlatılmaz.
  `AcquireTimeout`, aktif SMB I/O timeout'u değildir.
- Boştaki client'lar `IdleTimeout` sonrasında periyodik temizlikle kapatılır.
  Dosya bulunamadı/zaten var/erişim reddedildi gibi tanımlı normal dosya hataları
  bağlantıyı korur. Bilinmeyen native hata, transfer iptali veya close hatasında
  client ihtiyatlı biçimde çıkarılır; başarısız yazma otomatik tekrarlanmaz.
- Havuz kapatılınca yeni işlemler reddedilir ve boştaki client'lar kapatılır.
  `DisposeAsync`, uygulamanın açık tuttuğu stream'leri zorla kapatmaz veya onların
  kapanmasını beklemez; bu stream'ler bittiğinde client'ları da kapatılır.
- Her havuz tek sunucu/paylaşım/kimliğe aittir. Farklı kullanıcı/tenant kimlikleri
  için ayrı havuz kullanın. Parola rotasyonunda yeni havuz oluşturup eski havuzu
  kapatın; parolaları havuz anahtarlarına veya loglara koymayın.
- Sınırlar süreç/havuz başınadır. API replica sayısı ve kimlik havuzu sayısı
  arttığında NAS üzerindeki toplam bağlantı sayısı da artar.

`PooledSmbClient.GetStatistics()`, aktif kiralama, bekleyen istek, boştaki client,
oluşturulan/çıkarılan client, timeout, kuyruk reddi ve temizlik hatası sayaçlarının
anlık görüntüsünü verir. Örneğin doğrudan havuz örneğiyle
`var stats = pool.GetStatistics();` kullanılabilir. Bağlantı hataları mesaj metni
üzerinden eşleştirilmez; native katmanın `ConnectionReusable` bilgisi kullanılır.
Eski ABI v3 native dosyası bu alanı göndermiyorsa hatalı client güvenli varsayımla
havuzdan çıkarılır.

## Buffer havuzları

.NET tarafında stream ve yardımcı transfer buffer'ları `ArrayPool<byte>` ile
alınır ve transfer boyunca tekrar kullanılır; her chunk için yeni büyük dizi
oluşturulmaz. Havuzdan gelen dizinin gerçek uzunluğu daha büyük olsa da yalnızca
istenen chunk/count kadar veri kullanılır. `ClearBuffersOnReturn` varsayılan olarak
`true` değerindedir; iç managed diziler iade edilirken temizlenir. Bunu kapatmak
temizleme maliyetini azaltabilir fakat hassas verinin havuzda kalmasına izin verir.

Go async tarafında 64 KiB, 256 KiB, 1 MiB ve 4 MiB sınıflı buffer havuzu kullanılır.
Sınıf başına en fazla 16 buffer, toplamda en fazla **32 MiB boş native buffer**
tutulur; iade edilen buffer'ların tüm kapasitesi temizlenir. Read callback verisi
callback dönene kadar, write için sahipli kopya da işlem tamamlanana kadar
korunur. İptal isteği client/buffer'ı erken serbest bırakmaz.

Bu 32 MiB sınırı **toplam süreç belleği sınırı değildir**: kullanımda olan
buffer'lar, .NET shared array pool, SMB/runtime buffer'ları, farklı havuzlar ve
çağıranın dosya dizileri ayrıca bellek kullanır. Client havuzu eşzamanlı transfer
sayısını sınırlar; .NET ve Go bellek tüketimini birlikte izleyin. `ReadAsync`
sonucu yine dosyanın tamamını bellekte tutar; büyük dosyada streaming tercih edin.

Kullanıcının verdiği kaynak `byte[]` ve `Read`/`ReadAsync` ile dönen `Content`
dizileri iç havuzlara iade edilmez. Kaynak diziyi `WriteAsync` tamamlanmadan
değiştirmeyin veya kendi havuzunuza geri vermeyin. Buffer havuzu allocation/GC
yükünü azaltır; gereksiz kopyaları otomatik olarak kaldırmaz veya ağ hızında
belirli bir artışı garanti etmez.

## Stream ve chunk davranışı

`ISmbClient`, `IDisposable` ve `IAsyncDisposable` üzerinden kapatılabilir. Async
metotlar cancellation token kabul eder:

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

Mevcut senkron metotlar da kullanılabilir:

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

Stream'i kendiniz yönetmek istediğinizde:

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

Senkron kullanım:

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

Client ve açılan her stream için async kullanımda `await using`, senkron kullanımda
`using` kullanın. Stream'ler native client'ı kullanımda tutar; client dispose
edildiğinde yeni işlem başlatılamaz, açık stream'ler kapandığında native client da
serbest bırakılır. Normal kullanımda stream'leri client'tan önce kapatın.
`CopyTo`/`CopyToAsync` ve `Write`/`WriteAsync(path, Stream)` kendilerine verilen
yerel stream'i kapatmaz. `Flush`/`FlushAsync`, uzak dosya için SMB flush ister;
`ForceSync = true` başarılı yazmanın sonunda da flush uygular.

`ChunkSizeBytes` varsayılanı 1 MiB'dır; 1 byte ile 4 MiB arasında ayarlanabilir.
Stream transferinde dosya içeriği JSON/base64'e çevrilmez: her chunk doğrudan C
buffer'ı üzerinden taşınır. JSON yalnızca küçük kontrol/metadata yanıtlarında
kullanılır. Transfer için ek bellek dosya boyutuyla değil chunk boyutuyla büyür;
kullanıcının stream'i ve SMB/.NET çalışma zamanı buffer'ları ayrıca bellek
kullanabilir. `Read` ile dönen `byte[]` ve bellekte oluşturduğunuz kaynak içerik
ise doğal olarak tüm dosyayı bellekte tutar.

Stream'ler yalnızca ileri yönde ilerler; `Seek`, `Length` ve `Position`
desteklenmez. `ReadAsync`, `WriteAsync`, `FlushAsync`, `CopyToAsync` ve
`DisposeAsync` desteklenir. Async işlemler Go tarafındaki goroutine ve completion
callback üzerinden tamamlanır; bekleyen SMB işlemi için .NET tarafında `Task.Run`
ile bir worker thread tutulmaz. Yerel stream'in de asenkron I/O desteklemesi
gerekir; yukarıdaki `FileStream` örnekleri bunun için `useAsync: true` kullanır.

Cancellation token bekleyen işlemin başlamasını engelleyebilir ve chunk'lar
arasında kontrol edilir. Ancak kullanılan SMB kütüphanesinin aktif `Read`/`Write`
çağrısı ayrı bir cancellation context kabul etmez. Başlamış I/O anında kesilmez;
buffer'lar native katmanda kullanılmaya devam edebileceğinden iptal edilen await
da aktif native çağrı tamamlandıktan sonra sonuçlanır. İptal, daha önce yazılmış
veriyi geri almaz. Senkron `CopyTo` ve `Write(path, Stream)` iptali de çağrılar
arasında kontrol eder.

Yazmalar atomik değildir. Hata veya iptal, uzak dosyayı kısmen yazılmış halde
bırakabilir; `Overwrite = true` ile açmak mevcut dosyayı başta kısaltabilir.
Otomatik retry yapılmaz. Atomik yayınlama gerekiyorsa uygulama ayrı bir geçici
dosya ve rename iş akışı tasarlamalıdır.

Bir client'ın native işlemleri sıraya alınır; async API aynı client'ta paralel
SMB chunk I/O sağlamaz. Asıl kazanç, yüksek eşzamanlılıkta .NET thread'lerinin
ağ beklerken serbest kalmasıdır; tek dosyanın MB/s değerinin artacağı garanti
edilmez. Eşzamanlı bağımsız transferlerde sınırlı sayıda ayrı client kullanın;
her client kendi SMB oturumunu oluşturur. Uygulama eşzamanlılığı veya bir client
havuzunu sınırlamalıdır. Native async kabul sınırı süreç genelinde 1024 işlemdir;
sınıra gelince yeni işlem reddedilir, sınırsız kuyruk oluşturulmaz.

## Native ABI v3

.NET wrapper 1.2.0, async binary chunk ABI v3 gerektirir; wrapper ile native
dosyaları birlikte güncelleyin. ABI v1/v2 DLL/SO dosyası yeni wrapper ile
kullanılamaz. Eski
`StorageGO_CreateClient`, `StorageGO_Invoke`, `StorageGO_CloseClient` ve
`StorageGO_FreeResult` export'ları eski tüketiciler için korunur; eski JSON/base64
yolu büyük dosyalar için uygun değildir.

Senkron chunk export'ları korunur: `StorageGO_ABIVersion`, `StorageGO_OpenRead`,
`StorageGO_OpenWrite`, `StorageGO_ReadChunk`, `StorageGO_WriteChunk`,
`StorageGO_FlushTransfer` ve `StorageGO_CloseTransfer`. Her build, native
kütüphanenin yanında C ABI imzalarını içeren bir `.h` üretir.

Async ABI, `StorageGO_BeginAsync` ve `StorageGO_CancelAsync` export'larını ekler.
`BeginAsync`, işlemi kabul ettiğinde sıfırdan farklı bir operation ID döndürür;
başlatma reddedilirse `0` döner. Payload binary buffer üzerinden aktarılır;
write girdisi `BeginAsync` dönmeden native belleğe kopyalanır. Completion
callback'ine verilen sonuç ve read buffer'ı yalnızca callback süresince
geçerlidir; tüketici veriyi callback dönmeden kopyalamalı ve bu pointer'ları
serbest bırakmamalıdır. .NET wrapper veri ve callback bağlamı sahipliğini yönetir.
`StorageGO_ReleaseClient` ve `StorageGO_ReleaseTransfer`, SafeHandle/finalizer
temizliği için native kapatmayı bloklamadan başlatır; bu temizleme yolu async
kabul sınırından etkilenmez.

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

.NET/native senkron ve async chunk aktarımı testleri için .NET 8, Go, GCC ve
PowerShell 7 gerekir:

```powershell
pwsh -File ./tests/run-interop.ps1
```

Client havuzu testleri; kapasite/kuyruk sınırlarını, timeout ve cancellation'ı,
stream kiralama ömrünü, hata/idle durumunda client çıkarmayı ve eşzamanlı payload
bütünlüğünü de doğrular. Buffer testleri iade, temizleme, boyut ve callback
sahipliği kurallarını kapsar. Bunlar gerçek NAS performans benchmark'ı değildir.

Bu testler gerçek P/Invoke ve completion callback üzerinden üretimde kullanılan
chunk yönetimini yerel dosyalarla doğrular; SMB sunucusu veya kimlik bilgisi
gerektirmez. Test kütüphanesi ayrı bir dizine üretilir ve yalnızca test
uygulamasında yüklenir. Gerçek SMB
sunucusuyla ağ ve kimlik doğrulama entegrasyon testlerinin yerini tutmaz.

Go buffer havuzunun allocation mikro-benchmark'ı ayrıca çalıştırılabilir (NAS/ağ
throughput ölçümü değildir):

```powershell
go test -run '^$' -bench BenchmarkNativeBufferAllocation -benchmem
```

## GitHub Actions çıktıları

[Build workflow](.github/workflows/build-native.yml) her push, pull request veya
manuel çalıştırmada şu artifact'leri üretir:

- `storagego-linux-x64`: `.so` ve C header
- `storagego-win-x64`: `.dll` ve C header
- `StorageGO-dotnet-package`: her iki native runtime'ı içeren `.nupkg`

Her iki platformda Go race testleri, ABI header kontrolü ve .NET/native senkron
ve async chunk aktarım testleri çalıştırılır.

Windows job'ı `msys2/setup-msys2` ile UCRT64 GCC'yi kurar. Native test/build ve
interop adımları, action'ın bildirdiği kurulum dizininden gelen PATH ve `CC`
ayarlarını kullanır; runner'da önceden kurulu GCC veya sabit bir dizin varsayılmaz.

Parolayı loglamayın veya exception mesajlarına eklemeyin. Kimlik bilgilerini
güvenilir yapılandırmadan okuyun; client ve stream'leri deterministik olarak
dispose edin.
