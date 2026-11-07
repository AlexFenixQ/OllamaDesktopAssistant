. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Set-BuildEnvironment
$report = Assert-TestReport (Join-Path $Artifacts 'tests.json')
$smokePath = Join-Path $Artifacts 'smoke-test.json'
if (-not (Test-Path -LiteralPath $smokePath)) { throw 'Нет отчёта WPF smoke-test.' }
$smoke = Get-Content -LiteralPath $smokePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($smoke.version -ne $Version -or $smoke.passed -ne $true) { throw 'Проверка WPF относится к другой версии или не пройдена.' }
& (Join-Path $PSScriptRoot 'Prepare-Inno.ps1')
& (Join-Path $PSScriptRoot 'Collect-Notices.ps1')
foreach ($file in @('README_RU.md','CHANGELOG.md','SECURITY.md')) { Copy-Item -LiteralPath (Join-Path $Root $file) -Destination (Join-Path $Publish $file) -Force }
[pscustomobject]@{productId='OllamaDesktopAssistant';version=$Version;dataSchema=1;sourceBuild=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Publish 'release-installed.json') -Encoding UTF8
$files = @()
foreach ($file in Get-ChildItem -LiteralPath $Publish -File -Recurse | Where-Object { $_.Name -notin @('installed-files.json','.build-owned') }) {
    $files += [pscustomobject]@{path=$file.FullName.Substring($Publish.Length).TrimStart('\');bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
[pscustomobject]@{version=$Version;files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Publish 'installed-files.json') -Encoding UTF8
$installerDir = Join-Path $Artifacts 'installer'; New-OwnedDirectory $installerDir
$compiler = Join-Path $Tools 'inno\ISCC.exe'
Invoke-Checked $compiler @('/Qp', ('/DPublishDir=' + $Publish), ('/DOutputDir=' + $installerDir), (Join-Path $Root 'deployment\OllamaDesktop.iss')) 'packaging.log'
$setup = Join-Path $installerDir 'OllamaDesktop-0.1.31-win-x64-Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw 'Установщик не создан.' }
((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($setup)) | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ASCII
Write-Host ('Установщик: ' + $setup) -ForegroundColor Green
Write-Host 'Установщик пока не подписан сертификатом издателя. Не отключайте SmartScreen или антивирус. Публичное распространение — только после проверки и подписания.'
