param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
try {
    & (Join-Path $PSScriptRoot 'scripts\Deploy-Workspace.ps1') -NoBuild:$NoBuild
} catch {
    Write-Host 'Операция остановлена; предыдущие исходники не заменялись.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    Write-Host 'Сохраните текст ошибки без ключей доступа. Не удаляйте папки данных.'
    exit 1
}
