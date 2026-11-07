param([string]$TargetRoot = (Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant\Source\0.1.0'), [switch]$ConfirmRollback)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'SourceUpdate.Common.ps1')
$target = [IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
Assert-PlainPath $target
$active = @(Get-SourceUpdateAttempts $target | Where-Object { $_.state.status -ne 'rolledBack' })
if ($active.Count -eq 0) { Write-Host 'Активное обновление 0.1.30 не найдено. Исходники не изменялись.'; return }
if ($active.Count -ne 1) { throw 'Найдено несколько активных транзакций. Автоматический откат запрещён.' }
$record = $active[0]; $state = $record.state
$baselineFile = Get-BaselineFile ([string]$state.fromVersion)
if ($state.PSObject.Properties.Name -contains 'baselineFile') {
    $allowed = @($baselineFile)
    if ($state.fromVersion -eq '0.1.28') { $allowed += 'UPDATE_BASE_0128_HOTFIX1.json' }
    if ([string]$state.baselineFile -notin $allowed) { throw 'Неизвестная база отката.' }
    $baselineFile = [string]$state.baselineFile
}
$baseline = Get-Content -LiteralPath (Join-Path $Root $baselineFile) -Raw -Encoding UTF8 | ConvertFrom-Json
if ($baseline.productId -ne 'OllamaDesktopAssistant' -or $baseline.fromVersion -ne $state.fromVersion -or $baseline.toVersion -ne $Version) { throw 'Недостоверная база отката.' }
$backup = Join-Path $record.directory ('source-' + $state.fromVersion + '.zip')
Assert-PlainPath $backup
if (-not (Test-Path -LiteralPath $backup -PathType Leaf) -or (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $state.backupSha256) { throw 'Не найден проверенный архив исходников. Откат запрещён.' }
Assert-BackupArchive $backup @($baseline.files)
# The rollback only restores source files. Installed application and user data stay untouched.
$answer = if ($ConfirmRollback) { 'ДА' } else { Read-Host ('Восстановить исходники ' + $state.fromVersion + '? Пользовательские данные не изменятся. Введите ДА') }
if ($answer.Trim().ToUpperInvariant() -notin @('ДА','YES')) { Write-Host 'Откат отменён.'; return }
$lockPath = Join-Path $target '.source-operation.lock'
Assert-PlainPath $lockPath
$lock = $null
try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Restore-SourceBackup $target $backup @($baseline.files) @($state.newFiles)
    $state.status = 'rolledBack'
    Write-UpdateState $record.directory $state
    Write-Host ('Исходники ' + $state.fromVersion + ' восстановлены; данные, кеш и установленная программа не изменялись.') -ForegroundColor Green
} finally { if ($null -ne $lock) { $lock.Dispose() } }
