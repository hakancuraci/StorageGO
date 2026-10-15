[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runningOnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$runningOnLinux = if ($runningOnWindows) { $false } else {
    [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)
}
$fixtureSource = Join-Path $repoRoot 'artifacts/_interop-src'
$project = Join-Path $PSScriptRoot 'StorageGO.InteropTests/StorageGO.InteropTests.csproj'
$rid = if ($runningOnWindows) { 'win-x64' } elseif ($runningOnLinux) { 'linux-x64' } else { throw 'Windows or Linux is required.' }
$libraryName = if ($runningOnWindows) { 'storagego.dll' } else { 'libstoragego.so' }
$fixtureOutput = Join-Path $repoRoot "artifacts/interop-runtime/$rid"
$originalCGO = $env:CGO_ENABLED
$originalPath = $env:Path
$originalGOOS = $env:GOOS
$originalGOARCH = $env:GOARCH

try {
    if ($runningOnWindows -and -not (Get-Command gcc -ErrorAction SilentlyContinue)) {
        $gccDirectory = @(
            'C:\msys64\ucrt64\bin',
            'C:\msys64\mingw64\bin',
            'C:\ProgramData\chocolatey\lib\mingw\tools\install\mingw64\bin'
        ) | Where-Object { Test-Path (Join-Path $_ 'gcc.exe') } | Select-Object -First 1
        if (-not $gccDirectory) { throw 'CGO requires GCC on PATH.' }
        $env:Path = "$gccDirectory;$env:Path"
    }
    $env:CGO_ENABLED = '1'
    $env:GOOS = if ($runningOnWindows) { 'windows' } else { 'linux' }
    $env:GOARCH = 'amd64'
    New-Item -ItemType Directory -Force $fixtureSource, $fixtureOutput | Out-Null
    foreach ($source in @('go.mod', 'go.sum', 'main.go', 'smb_client.go', 'native_api.go', 'native_stream.go')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $source) -Destination (Join-Path $fixtureSource $source)
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'nativefixture/exports.go') -Destination (Join-Path $fixtureSource 'exports.go')
    $fixtureLibrary = Join-Path $fixtureOutput $libraryName
    Push-Location $fixtureSource
    try {
        & go build -trimpath -tags storagego_interop_fixture -buildmode=c-shared -o $fixtureLibrary .
        if ($LASTEXITCODE -ne 0) { throw 'Native interop fixture build failed.' }
    } finally {
        Pop-Location
    }
    & dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Interop test project build failed.' }
    $output = Join-Path $PSScriptRoot "StorageGO.InteropTests/bin/$Configuration/net8.0"
    $runtimeDirectory = Join-Path $output "runtimes/$rid/native"
    New-Item -ItemType Directory -Force $runtimeDirectory | Out-Null
    # Override only this test application's copy; production artifacts are never modified.
    Copy-Item -LiteralPath $fixtureLibrary -Destination (Join-Path $runtimeDirectory $libraryName) -Force
    & dotnet (Join-Path $output 'StorageGO.InteropTests.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Interop tests failed.' }
} finally {
    $env:CGO_ENABLED = $originalCGO
    $env:Path = $originalPath
    $env:GOOS = $originalGOOS
    $env:GOARCH = $originalGOARCH
}
