# Install the verified prebuilt release; no SDK or compiler is needed.
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
Assert-Environment
& (Join-Path $Root 'scripts\Verify-Package.ps1') -Quiet
$setup = Join-Path $Root 'release\OllamaDesktop-0.1.30-win-x64-Setup.exe'
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Готовый установщик отсутствует. Для сборки используйте Repair.ps1.' }
if (Test-Path -LiteralPath $InstallDir) {
    $files = @(Get-ChildItem -LiteralPath $InstallDir -Force)
    if ($files.Count -gt 0) {
        $marker = Join-Path $InstallDir 'release-installed.json'
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Неизвестная папка установки. Файлы не изменены.' }
        $release = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($release.productId -ne 'OllamaDesktopAssistant') { throw 'Неизвестное приложение. Файлы не изменены.' }
        if ($release.version -eq $Version) {
            Assert-InstalledRelease
            Start-Process -FilePath (Join-Path $InstallDir 'OllamaDesktop.exe')
            return
        }
        & (Join-Path $Root 'scripts\Update-Installed.ps1') -Setup $setup
        return
    }
}
$process = Start-Process -FilePath $setup -ArgumentList '/CURRENTUSER /NORESTART' -Wait -PassThru
if ($process.ExitCode -ne 0) { throw ('Установка не завершена: ' + $process.ExitCode) }
Write-Host 'Установка завершена. Откройте Ollama Desktop через меню «Пуск».' -ForegroundColor Green
