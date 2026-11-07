param([Parameter(Mandatory=$true)][string]$PrivateKeyPath,[ValidateSet('stable','beta')][string]$Channel='stable',[string]$MinimumVersion='0.1.30')
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
$setup=Join-Path $Artifacts ('installer\OllamaDesktop-'+$Version+'-win-x64-Setup.exe')
$inventory=Join-Path $Publish 'installed-files.json'
if(-not (Test-Path -LiteralPath $setup) -or -not (Test-Path -LiteralPath $inventory)){throw 'Сначала выполните сборку и Package.ps1.'}
$releaseDir=Join-Path $Artifacts 'signed-release'; New-OwnedDirectory $releaseDir
$base='https://github.com/AlexFenixQ/OllamaDesktopAssistant/releases/download/v'+$Version+'/'
$manifest=[ordered]@{productId='OllamaDesktopAssistant';version=$Version;channel=$Channel;minimumVersion=$MinimumVersion;installerUrl=$base+[IO.Path]::GetFileName($setup);installerSha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash;installerBytes=(Get-Item -LiteralPath $setup).Length;inventoryUrl=$base+'installed-files.json';inventorySha256=(Get-FileHash -LiteralPath $inventory -Algorithm SHA256).Hash;notes='Уточнённый интерфейс, документы до 512 МиБ, полная локальная расшифровка аудио/видео до 2 ГиБ и 6 часов, TXT и Word-отчёт. История, ключ и файлы сохраняются.'}
$json=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 5))
Add-Type -AssemblyName System.Security
$key=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PrivateKeyPath),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($key))
    $public=[IO.File]::ReadAllText((Join-Path $Root 'deployment\update-public-key.xml'))
    if($rsa.ToXmlString($false) -ne $public){throw 'Ключ не соответствует закреплённому открытому ключу приложения.'}
    $signature=$rsa.SignData($json,'SHA256')
    [IO.File]::WriteAllBytes((Join-Path $releaseDir 'update.json'),$json)
    [IO.File]::WriteAllBytes((Join-Path $releaseDir 'update.json.sig'),$signature)
    Copy-Item -LiteralPath $setup -Destination $releaseDir -Force
    Copy-Item -LiteralPath $inventory -Destination $releaseDir -Force
} finally { [Array]::Clear($key,0,$key.Length);$rsa.Dispose() }
Write-Host ('Подписанный релиз: '+$releaseDir)
