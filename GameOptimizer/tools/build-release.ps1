# Gera o pacote para distribuir:
#   release\GueibsOptimizer-<versão>\  (exe + LEIA-ME + avisos de licença + SHA256SUMS)
#   release\GueibsOptimizer-<versão>.zip
# Uso (na pasta Gueibs Optimizer): powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-release.ps1

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

[xml]$proj = Get-Content 'src\GueibsOptimizer\GueibsOptimizer.csproj'
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
Write-Host "== Gueibs Optimizer $version =="

Write-Host "Testes..."
dotnet test GueibsOptimizer.sln -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Os testes falharam; pacote não gerado." }

Write-Host "Publicando..."
dotnet publish src/GueibsOptimizer -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Falha no publish." }

$name = "GueibsOptimizer-$version"
$out = Join-Path $root "release\$name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Copy-Item 'dist\GueibsOptimizer.exe' $out
Copy-Item 'package\LEIA-ME.txt' $out
Copy-Item 'package\THIRD-PARTY-NOTICES.txt' $out

$hash = (Get-FileHash (Join-Path $out 'GueibsOptimizer.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), "$hash  GueibsOptimizer.exe`r`n", (New-Object System.Text.UTF8Encoding($false)))

$zip = Join-Path $root "release\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal

$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ""
Write-Host "Pacote: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host "SHA-256 do exe: $hash"
Write-Host "SHA-256 do zip: $zipHash"
