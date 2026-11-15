# StorageGO Text API

Bu örnek, `StorageGO.Client` paketini **NuGet.org üzerinden** indirerek bir SMB
paylaşımında UTF-8 metin okuyan ve yazan .NET 8 API'si sunar. Docker imajının build
ve runtime katmanları Ubuntu 24.04 (Noble) tabanlıdır.

## Çalıştırma

Docker Desktop açıkken depo kök dizininde:

```powershell
docker compose -f samples/StorageGO.TextApi/compose.yaml up --build -d
```

Verilen test SMB ayarları Compose dosyasında varsayılan olarak tanımlıdır. Container
içinde `127.0.0.1` API container'ının kendisini gösterdiğinden, host üzerindeki
`127.0.0.1:1445` adresine `host.docker.internal:1445` ile erişilir. Parolayı
varsayılan yerine ortamdan vermek için komuttan önce `$env:SMB_PASSWORD` ayarlanabilir.

## Endpoint'ler

Metin yazma:

```powershell
$body = @{
  path = "notes/hello.txt"
  content = "StorageGO üzerinden merhaba!"
  overwrite = $true
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
  -Uri http://localhost:8080/write `
  -ContentType "application/json; charset=utf-8" `
  -Body $body
```

Metin okuma:

```powershell
Invoke-RestMethod "http://localhost:8080/read?path=notes%2Fhello.txt"
```

Sağlık kontrolü:

```powershell
Invoke-RestMethod http://localhost:8080/health
```

Servis yalnızca paylaşım köküne göre göreli yolları kabul eder; `.` ve `..` yol
segmentleri reddedilir. Okuma ve yazma boyutu varsayılan olarak 16 MiB ile sınırlıdır.

Servisi durdurmak için:

```powershell
docker compose -f samples/StorageGO.TextApi/compose.yaml down
```
