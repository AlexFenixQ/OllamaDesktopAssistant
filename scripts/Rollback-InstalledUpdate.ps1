# Restore the previous installed version from a verified backup, without touching user data.
. (Join-Path $PSScriptRoot 'InstalledUpgrade.Common.ps1')
Assert-Environment
$receipts = @(Get-ChildItem -LiteralPath $Artifacts -Filter 'installed-app-backup-*.zip.receipt.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)
if ($receipts.Count -eq 0) { throw 'Не найдена проверяемая запись резервного копирования установленной версии.' }
$selected = $null
foreach ($receiptFile in $receipts) {
    $candidate = Get-Content -LiteralPath $receiptFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($candidate.productId -eq 'OllamaDesktopAssistant' -and $candidate.toVersion -eq $script:Version -and $candidate.state -in @('backup-complete','installed-and-verified')) {
        $selected = $candidate
        break
    }
}
if ($null -eq $selected) { throw 'Не найдена завершённая и проверенная запись обновления; автоматический откат остановлен.' }
$backup = [IO.Path]::GetFullPath([string]$selected.backup)
Assert-PlainPath $backup
if (-not $backup.StartsWith(($Artifacts.TrimEnd('\') + '\'),[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $backup -PathType Leaf) -or
    (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $selected.backupSha256) {
    throw 'Архив резервного копирования отсутствует или повреждён.'
}
$running = @(Get-Process -Name 'OllamaDesktop' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { throw 'Закройте Ollama Desktop и повторите команду отката.' }
$restoreStage = Join-Path ([IO.Path]::GetDirectoryName($script:InstallDir)) ('ODA.restore-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
Expand-SafeZip $backup $restoreStage
$oldState = Assert-InstalledAppInventory $restoreStage @([string]$selected.fromVersion)
Assert-InstallBackup $backup $oldState.manifest
$newInventoryPath = Join-Path $Publish 'installed-files.json'
if (-not (Test-Path -LiteralPath $newInventoryPath -PathType Leaf)) { $newInventoryPath = Join-Path $Root 'release\installed-files.json' }
$newInventory = Get-Content -LiteralPath $newInventoryPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($newInventory.version -ne $Version) { throw 'Нет манифеста новой версии для безопасного отката.' }
Assert-RecoverableInstall $script:InstallDir $oldState.manifest $newInventory $oldState.inventoryPath $newInventoryPath
# Validate the registry snapshot BEFORE moving application files.
if ($selected.registryExported) {
    $registrySnapshot = [IO.Path]::GetFullPath([string]$selected.registryBackup)
    Assert-PlainPath $registrySnapshot
    if (-not $registrySnapshot.StartsWith(($Artifacts.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $registrySnapshot -PathType Leaf) -or
        (Get-FileHash -LiteralPath $registrySnapshot -Algorithm SHA256).Hash -ne [string]$selected.registryBackupSha256) {
        throw 'Снимок реестра повреждён. Установленные файлы не изменены.'
    }
}
$answer = Read-Host ('Восстановить установленную версию ' + $oldState.version + '? Текущие файлы будут сохранены отдельно. Введите ДА')
if ($answer -cne 'ДА') { Write-Host 'Отменено. Подготовленная копия оставлена для диагностики.'; exit 0 }
$quarantine = $script:InstallDir + '.before-rollback-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
if (Test-Path -LiteralPath $quarantine) { throw 'Папка резервирования уже существует.' }
Move-Item -LiteralPath $script:InstallDir -Destination $quarantine
try { Move-Item -LiteralPath $restoreStage -Destination $script:InstallDir }
catch {
    if (-not (Test-Path -LiteralPath $script:InstallDir)) { Move-Item -LiteralPath $quarantine -Destination $script:InstallDir }
    throw
}
$restored = Assert-InstalledAppInventory $script:InstallDir @([string]$selected.fromVersion)
if ($selected.registryExported) {
    $registrySnapshot = [IO.Path]::GetFullPath([string]$selected.registryBackup)
    if (-not $registrySnapshot.StartsWith(($Artifacts.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $registrySnapshot -PathType Leaf) -or
        (Get-FileHash -LiteralPath $registrySnapshot -Algorithm SHA256).Hash -ne [string]$selected.registryBackupSha256) {
        throw 'Файлы восстановлены, но защищённый снимок реестра повреждён. Сохраните диагностику.'
    }
    & reg.exe import $registrySnapshot | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Файлы восстановлены, но запись деинсталлятора в HKCU не восстановилась.' }
} else {
    Write-Warning 'Прежняя запись деинсталлятора в HKCU отсутствовала; файлы восстановлены, но системную регистрацию необходимо проверить.'
}
Write-Host ('Восстановлена версия ' + $restored.version + '. Файлы заменённой версии сохранены: ' + $quarantine) -ForegroundColor Green
Write-Host 'История чатов и ключ Ollama не менялись.'
