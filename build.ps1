param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$localSdk = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
if (-not (& $dotnet --list-sdks)) { throw 'Install the .NET 8 SDK, or place it in .tools/dotnet.' }

function Invoke-Dotnet([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Push-Location $root
try {
    if (-not $SkipTests) { Invoke-Dotnet @('run', '--project', 'tests/FrameVault.Tests', '-c', 'Release') }
    if (-not $SkipTests) {
        Invoke-Dotnet @('build', 'tests/FrameVault.DesignChecks', '-c', 'Release', '-m:1', '-nodeReuse:false')
        Invoke-Dotnet @('run', '--project', 'tests/FrameVault.DesignChecks', '-c', 'Release', '--no-build', '--no-restore', '--', 'artifacts/previews')
    }
    $version = ([xml](Get-Content -LiteralPath 'src/FrameVault/FrameVault.csproj' -Raw)).Project.PropertyGroup.Version
    $output = Join-Path $root ('dist\FrameVault-' + $version)
    Invoke-Dotnet @('publish', 'src/FrameVault/FrameVault.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-m:1', '-nodeReuse:false', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $output)
    $documents = @('README.md', 'LICENSE', 'SECURITY.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'docs/USAGE.md', 'docs/FORMAT.md')
    foreach ($document in $documents) {
        $destination = Join-Path $output $document
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination $destination -Force
    }
    $zip = Join-Path $root 'dist\FrameVault-portable-win-x64.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in (@('FrameVault.exe') + $documents)) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $output $entry), $entry) | Out-Null
        }
    } finally { $archive.Dispose() }
    $checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zip + '.sha256') -Value "$checksum  FrameVault-portable-win-x64.zip" -Encoding ascii
    Write-Host "Portable package: $zip"
} finally { Pop-Location }
