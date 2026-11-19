# Valida cada id do catálogo com "winget show --id <id> -e" e gera:
#   src/GueibsOptimizer/Data/catalog.json  (somente ids existentes)
#   tools/catalog-report.md              (o que foi mantido/removido)
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-catalog.ps1
# Só faz consultas (winget show); não instala nada.

param(
    [string]$Candidates = "$PSScriptRoot\catalog-candidates.tsv",
    [string]$Output = "$PSScriptRoot\..\src\GueibsOptimizer\Data\catalog.json",
    [string]$Report = "$PSScriptRoot\catalog-report.md"
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$lines = Get-Content -LiteralPath $Candidates -Encoding UTF8 | Where-Object { $_.Trim() -ne '' }
$valid = New-Object System.Collections.Generic.List[object]
$removed = New-Object System.Collections.Generic.List[string]
$i = 0

foreach ($line in $lines) {
    $i++
    $parts = $line.Split("`t")
    if ($parts.Count -lt 4) { $removed.Add("$line (linha inválida)"); continue }
    $id = $parts[0].Trim()
    $out = & winget show --id $id -e --accept-source-agreements --disable-interactivity 2>&1 | Out-String
    $ok = ($LASTEXITCODE -eq 0) -and ($out -match [regex]::Escape("[$id]"))
    Write-Host ("[{0}/{1}] {2} {3}" -f $i, $lines.Count, ($(if ($ok) { 'OK ' } else { 'X  ' })), $id)
    if ($ok) {
        $valid.Add([ordered]@{ name = $parts[1].Trim(); wingetId = $id; category = $parts[2].Trim(); description = $parts[3].Trim() })
    } else {
        $removed.Add($id)
    }
}

$catalog = [ordered]@{ version = 1; validatedOn = (Get-Date -Format 'yyyy-MM-dd'); apps = $valid }
$json = $catalog | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Resolve-Path -LiteralPath (Split-Path $Output)).Path + '\' + (Split-Path $Output -Leaf), $json, (New-Object System.Text.UTF8Encoding($false)))

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Relatório de validação do catálogo")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Data: $(Get-Date -Format 'yyyy-MM-dd HH:mm'). Método: ``winget show --id <id> -e`` (fonte winget).")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("- Candidatos: $($lines.Count)")
[void]$sb.AppendLine("- Válidos (no catálogo): $($valid.Count)")
[void]$sb.AppendLine("- Removidos: $($removed.Count)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Removidos (id não encontrado no winget)")
foreach ($r in $removed) { [void]$sb.AppendLine("- ``$r``") }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Por categoria")
$valid | Group-Object { $_.category } | Sort-Object Name | ForEach-Object { [void]$sb.AppendLine("- $($_.Name): $($_.Count)") }
[System.IO.File]::WriteAllText($Report, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Validos: $($valid.Count) / Removidos: $($removed.Count)"
