. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Set-BuildEnvironment
Add-Type -AssemblyName System.IO.Compression.FileSystem
$notices = Join-Path $Publish 'ThirdPartyNotices'; New-OwnedDirectory $notices
$inventory = @()
foreach ($package in Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Filter '*.nuspec' -Recurse) {
    [xml]$spec = Get-Content -LiteralPath $package.FullName -Raw -Encoding UTF8
    $metadata = $spec.SelectSingleNode('//*[local-name()="metadata"]')
    $id = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
    $versionNode = $metadata.SelectSingleNode('*[local-name()="version"]')
    $packageVersion = $versionNode.InnerText
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    $licenseText = if ($null -ne $licenseNode) { $licenseNode.InnerText } else { 'SEE_PACKAGE_METADATA' }
    $dest = Join-Path $notices ($id + '-' + $packageVersion)
    New-OwnedDirectory $dest
    Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $dest 'package.nuspec') -Force
    $packageRoot = $package.Directory.FullName
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object { $_.Name -match '(?i)(license|licence|notice|copying|copyright)' -and $_.Extension -notin @('.dll','.pdb','.nupkg') }) {
        $relative = $file.FullName.Substring($packageRoot.Length).TrimStart('\')
        $target = Join-Path $dest $relative; New-OwnedDirectory ([IO.Path]::GetDirectoryName($target)); Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    $inventory += [pscustomobject]@{ id=$id; version=$packageVersion; license=$licenseText }
}
$inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $notices 'nuget-inventory.json') -Encoding UTF8
# Collect upstream native PDFium notices from the exact release; never silently substitute a different build.
$releaseFile = Join-Path $Tools 'pdfium-release-8111.json'
Get-OfficialFile 'https://api.github.com/repos/bblanchon/pdfium-binaries/releases/tags/chromium%2F8111' $releaseFile
$release = Get-Content -LiteralPath $releaseFile -Raw -Encoding UTF8 | ConvertFrom-Json
$asset = @($release.assets | Where-Object { $_.name -eq 'pdfium-win-x64.tgz' }) | Select-Object -First 1
if ($null -eq $asset -or -not ($asset.PSObject.Properties.Name -contains 'digest') -or $asset.digest -notmatch '^sha256:[0-9a-fA-F]{64}$') { throw 'Для точного PDFium-релиза не найден проверяемый архив лицензий. Выпуск установщика остановлен.' }
$bundle = Join-Path $Tools 'pdfium-win-x64-8111.tgz'; Get-OfficialFile $asset.browser_download_url $bundle
if ((Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash -ne $asset.digest.Substring(7)) { throw 'SHA-256 архива PDFium не совпадает.' }
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'; if (-not (Test-Path -LiteralPath $tar)) { throw 'Не найден штатный tar.exe Windows 11.' }
$entries = @(& $tar -tzf $bundle)
if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить архив PDFium.' }
foreach ($entry in $entries) { $p = $entry.Replace('\','/'); if ($p.StartsWith('/') -or $p.Contains(':') -or ($p.Split('/') -contains '..')) { throw 'Небезопасная структура PDFium-архива.' } }
$stage = Join-Path $Tools ('pdfium-notices-' + [Guid]::NewGuid().ToString('N')); New-OwnedDirectory $stage
try {
    Invoke-Checked $tar @('-xzf', $bundle, '-C', $stage) 'packaging.log'
    foreach ($item in Get-ChildItem -LiteralPath $stage -Recurse -Force) { Assert-PlainPath $item.FullName }
    $native = Get-ChildItem -LiteralPath $stage -Filter 'pdfium.dll' -Recurse | Select-Object -First 1
    if ($null -eq $native -or (Get-FileHash -LiteralPath $native.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Publish 'pdfium.dll') -Algorithm SHA256).Hash) { throw 'DLL PDFium не совпадает с архивом лицензий закреплённого релиза.' }
    $pdfiumNotices = Join-Path $notices 'PDFium-native-158.0.8111'; New-OwnedDirectory $pdfiumNotices
    $count = 0
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object { ($_.Name -match '(?i)(license|licence|notice|copying|copyright)' -or $_.FullName.Substring($stage.Length) -match '(?i)[\\/](licenses|licences|notices)[\\/]') -and $_.Extension -notin @('.dll','.lib','.a','.ttf','.otf','.woff','.woff2') }) {
        $relative = $file.FullName.Substring($stage.Length).TrimStart('\'); $target = Join-Path $pdfiumNotices $relative
        New-OwnedDirectory ([IO.Path]::GetDirectoryName($target)); Copy-Item -LiteralPath $file.FullName -Destination $target -Force; $count++
    }
    if ($count -eq 0) { throw 'Тексты лицензий PDFium отсутствуют. Выпуск остановлен.' }
    Copy-Item -LiteralPath $releaseFile -Destination (Join-Path $pdfiumNotices 'upstream-release.json') -Force
} finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }
foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    $file = Join-Path $env:DOTNET_ROOT $name
    if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $notices ('Microsoft-dotnet-' + $name)) -Force }
}
Copy-Item -LiteralPath (Join-Path $Root 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $notices 'README.md') -Force
Write-Host 'Сведения о зависимостях и доступные upstream-лицензии включены. Юридическая проверка перед публичным выпуском остаётся отдельным этапом.'
