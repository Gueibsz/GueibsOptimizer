# Gera os ícones do Gueibs Optimizer a partir do símbolo original do projeto
# (quadrado arredondado + raio; desenho próprio, sem arte de terceiros):
#   src/GueibsOptimizer/Resources/app.ico          ícone do .exe (carmim, a cor de destaque padrão; 16 a 256 px)
#   src/GueibsOptimizer/Assets/Icons/icon-<cor>.ico uma variante por cor da paleta (16, 32, 48, 256 px)
# O raio fica escuro ou claro conforme o contraste com a cor de fundo (mesma regra do tema).
# O mesmo desenho existe em C# (Views/IconRenderer.cs) para cores personalizadas em tempo de execução.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$palette = [ordered]@{
    'carmim' = '#C52613'; 'ciano' = '#22D3EE'; 'azul' = '#3B82F6'; 'indigo' = '#6366F1'; 'roxo' = '#A855F7'; 'rosa' = '#EC4899'
    'laranja' = '#F97316'; 'ambar' = '#F59E0B'; 'verde' = '#22C55E'; 'verde-agua' = '#14B8A6'
}

function ConvertFrom-Hex([string]$hex) {
    $h = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb([Convert]::ToInt32($h.Substring(0, 2), 16), [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16))
}

function Get-Luminance([System.Drawing.Color]$c) {
    $ch = { param($v) $s = $v / 255.0; if ($s -le 0.03928) { $s / 12.92 } else { [Math]::Pow(($s + 0.055) / 1.055, 2.4) } }
    0.2126 * (& $ch $c.R) + 0.7152 * (& $ch $c.G) + 0.0722 * (& $ch $c.B)
}

function Get-BoltColor([System.Drawing.Color]$bg) {
    $dark = [System.Drawing.Color]::FromArgb(0x0B, 0x12, 0x16)
    $white = [System.Drawing.Color]::White
    $lb = Get-Luminance $bg
    $cDark = ([Math]::Max($lb, (Get-Luminance $dark)) + 0.05) / ([Math]::Min($lb, (Get-Luminance $dark)) + 0.05)
    $cWhite = ([Math]::Max($lb, 1.0) + 0.05) / ([Math]::Min($lb, 1.0) + 0.05)
    if ($cDark -ge $cWhite) { $dark } else { $white }
}

function New-IconImage([int]$s, [System.Drawing.Color]$bg) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $k = $s / 32.0
    $r = [Math]::Max(2, 10 * $k)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(1 * $k, 1 * $k, $r, $r, 180, 90)
    $path.AddArc($s - 1 * $k - $r, 1 * $k, $r, $r, 270, 90)
    $path.AddArc($s - 1 * $k - $r, $s - 1 * $k - $r, $r, $r, 0, 90)
    $path.AddArc(1 * $k, $s - 1 * $k - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $bg), $path)
    $pts = @(
        (New-Object System.Drawing.PointF (18 * $k), (4 * $k)),
        (New-Object System.Drawing.PointF (8 * $k), (18 * $k)),
        (New-Object System.Drawing.PointF (15 * $k), (18 * $k)),
        (New-Object System.Drawing.PointF (12 * $k), (28 * $k)),
        (New-Object System.Drawing.PointF (24 * $k), (13 * $k)),
        (New-Object System.Drawing.PointF (17 * $k), (13 * $k))
    )
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (Get-BoltColor $bg)), [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# Formato ICO: cabeçalho + diretório + imagens PNG (aceito pelo Windows Vista+ e pelo WPF).
function Save-Ico([string]$path, [int[]]$sizes, [System.Drawing.Color]$bg) {
    $images = foreach ($s in $sizes) { , @($s, (New-IconImage $s $bg)) }
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $s = $img[0]; $data = $img[1]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
    $bw.Close()
    Write-Host "Ícone: $path"
}

Save-Ico (Join-Path $root 'src\GueibsOptimizer\Resources\app.ico') @(16, 24, 32, 48, 64, 128, 256) (ConvertFrom-Hex $palette['carmim'])
foreach ($name in $palette.Keys) {
    Save-Ico (Join-Path $root "src\GueibsOptimizer\Assets\Icons\icon-$name.ico") @(16, 32, 48, 256) (ConvertFrom-Hex $palette[$name])
}
