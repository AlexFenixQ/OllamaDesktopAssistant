param([Parameter(Mandatory=$true)][string]$Setup)
. (Join-Path $PSScriptRoot 'InstalledUpgrade.Common.ps1')
Assert-Environment
New-OwnedDirectory $Artifacts
Assert-PlainPath $script:InstallDir
if (-not (Test-Path -LiteralPath $Setup -PathType Leaf) -or (Get-Item -LiteralPath $Setup).Length -lt 100000) {
    throw 'Новый Setup.exe отсутствует или поврежден.'
}
$known = @('0.1.0','0.1.1','0.1.2','0.1.3','0.1.4','0.1.5','0.1.6','0.1.7','0.1.8','0.1.9','0.1.10','0.1.11','0.1.12','0.1.13','0.1.14','0.1.15','0.1.16','0.1.17','0.1.18','0.1.19','0.1.20','0.1.21','0.1.22','0.1.23','0.1.24','0.1.25','0.1.26','0.1.27','0.1.28','0.1.29')
$before = Assert-InstalledAppInventory $script:InstallDir $known
$newManifestPath = Join-Path $Publish 'installed-files.json'
if (-not (Test-Path -LiteralPath $newManifestPath -PathType Leaf)) { $newManifestPath = Join-Path $Root 'release\installed-files.json' }
if (-not (Test-Path -LiteralPath $newManifestPath -PathType Leaf)) { throw 'Отсутствует манифест нового приложения.' }
$newManifest = Get-Content -LiteralPath $newManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-InPlaceUpgradeCompatible $before.manifest $newManifest
$busy = @(Get-Process -Name 'OllamaDesktop' -ErrorAction SilentlyContinue)
if ($busy.Count -gt 0) { throw 'Закройте Ollama Desktop обычным способом и повторно запустите готовый сценарий. Процессы принудительно не завершаются.' }
$choice = Read-Host ('Обновить установленную версию ' + $before.version + ' до ' + $script:Version + '? Данные сохраняются. Введите ДА')
if ($choice -cne 'ДА') { throw 'Обновление отменено пользователем.' }
$backup = Join-Path $Artifacts ('installed-app-backup-' + $before.version + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.zip')
$backupSha = New-VerifiedInstallBackup $script:InstallDir $backup $before.manifest
Write-Host ('Проверенная резервная копия: ' + $backup) -ForegroundColor Green
$receiptPath = $backup + '.receipt.json'
$regBackup = $backup + '.uninstall.reg'
$regExported = $false
# HKCU key is per-user and can be restored alongside the saved Inno uninstall log.
$registryPath = $script:UninstallRegistryKey
$registryProviderPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4959D6E5-8A31-45A2-B215-992E064C87B9}_is1'
if (Test-Path -LiteralPath $registryProviderPath) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $null = & reg.exe export $registryPath $regBackup /y 2>&1
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $regBackup -PathType Leaf)) { $regExported = $true }
    } finally { $ErrorActionPreference = $previousPreference }
}
$regBackupSha = if ($regExported) { (Get-FileHash -LiteralPath $regBackup -Algorithm SHA256).Hash } else { '' }
$receipt = [pscustomobject]@{productId='OllamaDesktopAssistant';fromVersion=$before.version;toVersion=$script:Version;
  backup=$backup;backupSha256=$backupSha;registryBackup=$regBackup;registryBackupSha256=$regBackupSha;registryExported=$regExported;
  state='backup-complete';utc=[DateTime]::UtcNow.ToString('o')}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
$permit = $null
try {
    # Inno Setup 7 upgrades same-AppId installations in place; no uninstall first.
    # Recheck file hashes immediately before issuing a one-use install permit.
    $null = Assert-InstalledAppInventory $script:InstallDir @($before.version)
    $permit = New-UpgradePermit $script:InstallDir $script:Version
    $installerLog = Join-Path $Artifacts ('inno-upgrade-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.log')
    $args = @('/CURRENTUSER','/NORESTART', ('/ODAVERIFY=' + $permit.token), ('/DIR="' + $script:InstallDir + '"'), ('/LOG="' + $installerLog + '"'))
    $result = Start-Process -FilePath $Setup -ArgumentList $args -Wait -PassThru
    if ($result.ExitCode -ne 0) { throw ('Установщик остановился с кодом ' + $result.ExitCode + '. Резервная копия сохранена.') }
    Remove-ObsoleteInstallFiles $script:InstallDir $before.manifest $newManifest
    $after = Assert-InstalledAppInventory $script:InstallDir @($script:Version)
    Assert-InPlaceUpgradeCompatible $newManifest $after.manifest
    foreach ($file in @($newManifest.files)) {
        $installed = @($after.manifest.files | Where-Object { $_.path -eq $file.path })
        if ($installed.Count -ne 1 -or $installed[0].sha256 -ne $file.sha256) {
            throw ('После установки не совпадает файл ' + $file.path)
        }
    }
    $receipt.state = 'installed-and-verified'
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
    Write-Host 'Установлено обновление, файлы проверены. Старый архив и данные пользователя сохранены.' -ForegroundColor Green
} catch {
    Write-Warning 'Установка или проверка завершилась ошибкой. Резервная копия сохранена. Автоматическое удаление файлов не выполнялось.'
    Write-Warning ('Для проверяемого отката предусмотрен Rollback-InstalledUpdate.ps1 и резервный архив: ' + $backup)
    throw
} finally {
    if ($null -ne $permit -and (Test-Path -LiteralPath $permit.path -PathType Leaf)) {
        $original = @([IO.File]::ReadAllLines($permit.path,[Text.Encoding]::ASCII))
        if ($original.Count -eq 5 -and $original[2] -eq $permit.token) { Remove-Item -LiteralPath $permit.path -Force }
    }
}
