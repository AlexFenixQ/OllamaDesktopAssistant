# Program-file simulation only; does not execute Setup or touch user data.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InstalledUpgrade.Common.ps1')
$before=Assert-InstalledAppInventory $InstallDir @('0.1.30')
$originalInventoryHash=(Get-FileHash -LiteralPath $before.inventoryPath -Algorithm SHA256).Hash
$fixture=Join-Path $env:LOCALAPPDATA ('ODA.u-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
if(Test-Path -LiteralPath $fixture){throw 'Test directory exists'}
$expectedParent=[IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')+'\'
if(-not [IO.Path]::GetFullPath($fixture).StartsWith($expectedParent,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fixture) -notmatch '^ODA\.u-[a-f0-9]{8}$'){throw 'Unsafe fixture path'}
New-OwnedDirectory $fixture
try {
 $clone=Join-Path $fixture 'app'; New-OwnedDirectory $clone
 foreach($item in Get-ChildItem -LiteralPath $InstallDir -Force){Copy-Item -LiteralPath $item.FullName -Destination $clone -Recurse}
 $oldManifest=Join-Path $fixture 'old-inventory.json'; Copy-Item -LiteralPath $before.inventoryPath -Destination $oldManifest
 $backup=Join-Path $fixture 'backup.zip'
 $null=New-VerifiedInstallBackup $clone $backup $before.manifest
 $newManifestPath=Join-Path $Publish 'installed-files.json'
 $newManifest=Get-Content -LiteralPath $newManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
 Assert-InPlaceUpgradeCompatible $before.manifest $newManifest
 Copy-Item -LiteralPath (Join-Path $Publish 'OllamaDesktop.dll') -Destination (Join-Path $clone 'OllamaDesktop.dll') -Force
 Assert-RecoverableInstall $clone $before.manifest $newManifest $oldManifest $newManifestPath
 foreach($entry in @($newManifest.files)) {
  $destination=Join-Path $clone $entry.path; New-OwnedDirectory ([IO.Path]::GetDirectoryName($destination))
  Copy-Item -LiteralPath (Join-Path $Publish $entry.path) -Destination $destination -Force
 }
 Copy-Item -LiteralPath $newManifestPath -Destination (Join-Path $clone 'installed-files.json') -Force
 Remove-ObsoleteInstallFiles $clone $before.manifest $newManifest
 $after=Assert-InstalledAppInventory $clone @($Version)
 $restore=Join-Path $fixture 'restore'; Expand-SafeZip $backup $restore
 $oldAgain=Assert-InstalledAppInventory $restore @($before.version)
 if((Get-FileHash -LiteralPath $before.inventoryPath -Algorithm SHA256).Hash -ne $originalInventoryHash){throw 'Actual installation inventory changed'}
 [pscustomobject]@{version=$Version;passed=$true;realInstalledBase=$before.version;oldFiles=@($before.manifest.files).Count;newFiles=@($after.manifest.files).Count;checks=@('real installed inventory verified read-only','program-only clone backup','partial upgrade accepted for rollback','full file transition with obsolete cleanup','backup extraction and old inventory verification','real installation inventory unchanged');actualInstallerExecuted=$false;userDataAccessed=$false} |
  ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Artifacts 'installed-inventory-transition.json') -Encoding UTF8
 Write-Host 'PASS: program inventory transition 0.1.30 -> 0.1.31 and verified backup restoration, real install unchanged'
} finally { if(Test-Path -LiteralPath $fixture){Assert-PlainPath $fixture; Remove-Item -LiteralPath $fixture -Recurse -Force} }
