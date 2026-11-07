# Owner workflow. No source editing, administrator account or globally installed SDK is required.
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
$operationLock = $null
try {
    Assert-Environment
    $operationLockPath = Join-Path $Root '.source-operation.lock'
    Assert-PlainPath $operationLockPath
    $operationLock = [IO.File]::Open($operationLockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    & (Join-Path $Root 'scripts\Verify-Package.ps1')
    $hasExisting = (Test-Path -LiteralPath $InstallDir) -and (@(Get-ChildItem -LiteralPath $InstallDir -Force).Count -gt 0)
    if ($hasExisting) {
        $marker = Join-Path $InstallDir 'release-installed.json'
        if (-not (Test-Path -LiteralPath $marker)) { throw 'В папке установки неизвестное приложение; ничего не изменено.' }
        $installedVersion = (Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json).version
        if ($installedVersion -eq $Version) {
            Assert-InstalledRelease
            Write-Host 'Текущая версия приложения уже установлена.'
            Start-Process -FilePath (Join-Path $InstallDir 'OllamaDesktop.exe'); exit 0
        }
        if ($installedVersion -notin @('0.1.0','0.1.1','0.1.2','0.1.3','0.1.4','0.1.5','0.1.6','0.1.7','0.1.8','0.1.9','0.1.10','0.1.11','0.1.12','0.1.13','0.1.14','0.1.15','0.1.16','0.1.17','0.1.18','0.1.19','0.1.20','0.1.21','0.1.22','0.1.23','0.1.24','0.1.25','0.1.26','0.1.27','0.1.28','0.1.29','0.1.30','0.1.31')) {
            throw 'Версия установленного приложения неизвестна. Автоматическое обновление запрещено.'
        }
        Write-Host ('Обнаружена установленная версия ' + $installedVersion + '. Сначала будет собран новый Setup.exe.')
    }
    Write-Host 'Будут загружены официальный .NET SDK и NuGet-зависимости; затем выполнены тесты, сборка приложения и установщика. API-ключ в терминале не нужен.'
    & (Join-Path $Root 'scripts\Test-Bootstrap.ps1')
    & (Join-Path $Root 'scripts\Build.ps1')
    & (Join-Path $Root 'scripts\Package.ps1')
    $setup = Join-Path $Artifacts 'installer\OllamaDesktop-0.1.32-win-x64-Setup.exe'
    if ($hasExisting) {
        & (Join-Path $Root 'scripts\Update-Installed.ps1') -Setup $setup
        if ($LASTEXITCODE -ne 0) { throw 'Обновление установленного приложения завершилось ошибкой.' }
    } else {
        $process = Start-Process -FilePath $setup -ArgumentList '/CURRENTUSER /NORESTART' -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw 'Установка приложения отменена или не завершена.' }
    }
    Write-Host 'Установка завершена. Откройте Ollama Desktop в меню «Пуск», введите ключ в настройках и проверьте доступ.' -ForegroundColor Green
} catch {
    Write-Host 'Операция остановлена. Не редактируйте исходники и не удаляйте папки данных.' -ForegroundColor Red
    $failure = $_
    Write-Host $failure.Exception.Message -ForegroundColor Yellow
    try { Save-BuildFailure $failure } catch { Write-Warning 'Не удалось записать подробности ошибки.' }
    try { & (Join-Path $Root 'scripts\Collect-Diagnostics.ps1') } catch { Write-Host 'Диагностический архив создать не удалось. Сохраните текст ошибки без ключей доступа.' }
    exit 1
} finally { if ($null -ne $operationLock) { $operationLock.Dispose() } }
