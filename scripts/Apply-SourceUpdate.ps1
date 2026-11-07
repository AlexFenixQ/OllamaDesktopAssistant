param([string]$TargetRoot = (Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant\Source\0.1.0'))
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'SourceUpdate.Common.ps1')
Assert-Environment
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Quiet
$packageRoot = $Root
$target = [IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
Assert-PlainPath $target
if ($target -eq $packageRoot) { throw 'Распакуйте исправление в отдельную папку.' }
if (-not (Test-Path -LiteralPath (Join-Path $target 'RELEASE.json') -PathType Leaf)) {
    throw 'Не найдена установленная база исходников. Для чистой установки используйте Start.ps1 из архива 0.1.31.'
}
$newFiles = @(Get-CurrentInventory $packageRoot)
$release = Get-Content -LiteralPath (Join-Path $target 'RELEASE.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($release.productId -ne 'OllamaDesktopAssistant') { throw 'Неизвестный проект. Замена запрещена.' }
if ($release.version -eq $Version) {
    Assert-SourceInventory $target $newFiles
    Write-Host 'Версия 0.1.31 уже установлена в исходниках; контрольные суммы совпадают.' -ForegroundColor Green
    return
}
$baselineName = Get-SourceBaselineFile ([string]$release.version) $target
$baseline = Get-Content -LiteralPath (Join-Path $packageRoot $baselineName) -Raw -Encoding UTF8 | ConvertFrom-Json
if ($baseline.productId -ne 'OllamaDesktopAssistant' -or $baseline.fromVersion -ne $release.version -or $baseline.toVersion -ne $Version) { throw 'Неверный манифест базовой версии.' }
$oldFiles = @($baseline.files)
if (@($oldFiles | Where-Object { $_.path -eq 'MANIFEST.json' }).Count -ne 1) { throw 'База не подтверждает право заменить MANIFEST.json.' }
# Installed application is untouched during source upgrade. Start.ps1 handles a separate verified application update.
$lockPath = Join-Path $target '.source-operation.lock'
Assert-PlainPath $lockPath
$lock = $null
try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    # Files are checked before ANY replacement, including the release marker.
    Assert-SourceInventory $target $oldFiles
    $active = @(Get-SourceUpdateAttempts $target | Where-Object { $_.state.status -ne 'rolledBack' })
    if ($active.Count -gt 0) { throw 'Есть незавершённая транзакция 0.1.31. Запустите Rollback-SourceUpdate.ps1 из исправления; файлы не изменены.' }
    Assert-NoUnknownCollisions $target $oldFiles $newFiles
    $newMap = @{}
    foreach ($entry in $newFiles) { $newMap[$entry.path] = $entry }
    # The obsolete 0.1.1 transaction is intentionally NOT read, moved or removed.
    $backupRoot = New-SourceUpdateAttempt $target $release.version
    New-OwnedDirectory $backupRoot
    $backup = Join-Path $backupRoot ('source-' + $release.version + '.zip')
    New-SourceBackup $target $backup $oldFiles
    Assert-BackupArchive $backup $oldFiles
    $state = [pscustomobject]@{
        productId='OllamaDesktopAssistant'; fromVersion=$release.version; toVersion=$Version
        baselineFile=$baselineName
        status='prepared'; utc=[DateTime]::UtcNow.ToString('o')
        backupSha256=(Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash; newFiles=$newFiles
    }
    Write-UpdateState $backupRoot $state
    Write-Host ('Базовая версия ' + $release.version + ' проверена; резервная копия создана.') -ForegroundColor Green
    try {
        $state.status = 'applying'
        Write-UpdateState $backupRoot $state
        foreach ($entry in $newFiles) {
            $source = Get-ManagedPath $packageRoot $entry.path
            $destination = Get-ManagedPath $target $entry.path
            New-OwnedDirectory ([IO.Path]::GetDirectoryName($destination))
            $temporary = Join-Path ([IO.Path]::GetDirectoryName($destination)) ('.u-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
            try {
                Copy-Item -LiteralPath $source -Destination $temporary
                if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Повреждён копируемый файл.' }
                Move-Item -LiteralPath $temporary -Destination $destination -Force
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
        }
        # Remove only obsolete files explicitly covered by the verified old manifest.
        foreach ($entry in $oldFiles) {
            if (-not $newMap.ContainsKey($entry.path)) {
                $obsolete = Get-ManagedPath $target $entry.path
                if ((Get-FileHash -LiteralPath $obsolete -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Неожиданно изменён устаревший файл: ' + $entry.path) }
                Remove-Item -LiteralPath $obsolete -Force
            }
        }
        Assert-SourceInventory $target $newFiles
        $state.status = 'applied'
        Write-UpdateState $backupRoot $state
    } catch {
        $failure = $_
        Write-Warning 'Ошибка обновления. Выполняется откат исходных файлов из проверенной копии.'
        try {
            Restore-SourceBackup $target $backup $oldFiles $newFiles
            $state.status = 'rolledBack'
            Write-UpdateState $backupRoot $state
        } catch {
            throw ('Не удалось автоматически восстановить исходники: ' + $_.Exception.Message + '. Для отката используйте Rollback-SourceUpdate.ps1 из архива 0.1.31. Первоначальная ошибка: ' + $failure.Exception.Message)
        }
        throw $failure
    }
    Write-Host 'Исправление 0.1.31 применено. Старая папка незавершённого обновления и пользовательские данные сохранены.' -ForegroundColor Green
    Write-Host ('Резервная копия: ' + $backup)
} finally { if ($null -ne $lock) { $lock.Dispose() } }
