# Gera o instalador: dist\GueibsOptimizer-Setup-<versão>.exe
#   1. dotnet publish (single-file, self-contained) -> dist\GueibsOptimizer.exe
#   2. iscc installer\GueibsOptimizer.iss (Inno Setup 6)
# Uso (na pasta Gueibs Optimizer): powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-installer.ps1
# O script NÃO instala o Inno Setup: se o iscc não for encontrado, ele explica como instalar e para.

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

[xml]$proj = Get-Content 'src\GueibsOptimizer\GueibsOptimizer.csproj'
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "Versão não encontrada no GueibsOptimizer.csproj." }
Write-Host "== Gueibs Optimizer ${version}: instalador =="

function Find-Iscc {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # Pastas padrão do Inno Setup 6 e 7 (o pacote do winget passou para a 7).
    $candidates = foreach ($v in 7, 6) {
        "${env:ProgramFiles(x86)}\Inno Setup $v\ISCC.exe"
        "$env:ProgramFiles\Inno Setup $v\ISCC.exe"
        "$env:LOCALAPPDATA\Programs\Inno Setup $v\ISCC.exe"
    }
    # Pasta escolhida na instalação, lida do registro de programas instalados.
    $uninstall = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
                 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $candidates += Get-ItemProperty $uninstall -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } |
        ForEach-Object { Join-Path $_.InstallLocation 'ISCC.exe' }
    return $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup (6 ou 7) não encontrado (ISCC.exe)." -ForegroundColor Yellow
    Write-Host "Instale pelo winget e rode este script de novo:"
    Write-Host "    winget install --id JRSoftware.InnoSetup -e"
    Write-Host "ou baixe em https://jrsoftware.org/isdl.php"
    exit 2
}

Write-Host "Publicando..."
dotnet publish src/GueibsOptimizer -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Falha no publish." }

Write-Host "Compilando o instalador com $iscc ..."
$exe = (Resolve-Path 'dist\GueibsOptimizer.exe').Path
& $iscc "/DAppVersion=$version" "/DSourceExe=$exe" 'installer\GueibsOptimizer.iss'
if ($LASTEXITCODE -ne 0) { throw "O iscc falhou (código $LASTEXITCODE)." }

$setup = Join-Path $root "dist\GueibsOptimizer-Setup-$version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ""
Write-Host "Instalador: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host "SHA-256: $hash"
