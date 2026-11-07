. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Quiet
& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
Set-BuildEnvironment
& (Join-Path $PSScriptRoot 'Build-Native.ps1')
Push-Location $Root
try {
    $app = Join-Path $Root 'src\OllamaDesktop.App\OllamaDesktop.App.csproj'
    $tests = Join-Path $Root 'tests\OllamaDesktop.Tests\OllamaDesktop.Tests.csproj'
    $nuget = Join-Path $Root 'NuGet.Config'
    Write-Host 'Восстановление закреплённых зависимостей и проверка известных серьёзных уязвимостей…'
    Assert-ReleaseContracts
    Invoke-Checked $Dotnet @('restore', $app, '-r', 'win-x64', '--locked-mode', '--configfile', $nuget)
    Invoke-Checked $Dotnet @('restore', $tests, '-r', 'win-x64', '--locked-mode', '--configfile', $nuget)
    Invoke-Checked $Dotnet @('build', $tests, '-c', 'Release', '-r', 'win-x64', '--no-restore')
    $testDll = Join-Path $Root 'tests\OllamaDesktop.Tests\bin\Release\net10.0\win-x64\OllamaDesktop.Tests.dll'
    Invoke-Checked $Dotnet @($testDll, '--report', (Join-Path $Artifacts 'tests.json')) 'tests.log'
    $testReport = Assert-TestReport (Join-Path $Artifacts 'tests.json')
    Write-Host ('Отчёт тестов подтверждён: ' + $testReport.count + '/' + (Get-ReleaseTestCount) + ', версия ' + $testReport.version)
    if (Test-Path -LiteralPath $Publish) {
        if (-not (Test-Path -LiteralPath (Join-Path $Publish '.build-owned'))) { throw 'Неизвестная папка публикации не будет перезаписана.' }
        Assert-PlainPath $Publish
        Remove-Item -LiteralPath $Publish -Recurse -Force
    }
    New-OwnedDirectory $Publish
    Set-Content -LiteralPath (Join-Path $Publish '.build-owned') -Value 'OllamaDesktopAssistant 0.1.32' -Encoding ASCII
    Invoke-Checked $Dotnet @('publish', $app, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-o', $Publish, '-p:PublishTrimmed=false', '-p:PublishSingleFile=false')
    foreach ($required in @('OllamaDesktop.exe','OllamaDesktop.dll','coreclr.dll','hostfxr.dll','e_sqlite3.dll','pdfium.dll','policy.xml')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Publish $required))) { throw ('В публикации отсутствует обязательная зависимость: ' + $required) }
    }
    if (@(Get-ChildItem -LiteralPath $Publish -Filter '*Magick*Native*.dll').Count -eq 0) { throw 'Не найдена нативная библиотека Magick.NET.' }
    Write-Host 'Проверка запуска WPF на вашем Windows (без облачных запросов)…'
    $process = Start-Process -FilePath (Join-Path $Publish 'OllamaDesktop.exe') -ArgumentList '--smoke-test' -WorkingDirectory $Publish -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(90 * 1000)) { $process.Kill(); throw 'WPF smoke-test превысил 90 секунд.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Publish 'smoke-test.json'))) { throw 'WPF smoke-test не прошёл. Установщик не выпускается.' }
    Move-Item -LiteralPath (Join-Path $Publish 'smoke-test.json') -Destination (Join-Path $Artifacts 'smoke-test.json') -Force
    $lockDir = Join-Path $Artifacts 'dependency-locks'; New-OwnedDirectory $lockDir
    foreach ($project in @('src\OllamaDesktop.App','src\OllamaDesktop.Core','tests\OllamaDesktop.Tests')) {
        $lock = Join-Path (Join-Path $Root $project) 'packages.lock.json'
        if (Test-Path -LiteralPath $lock) { Copy-Item -LiteralPath $lock -Destination (Join-Path $lockDir ((Split-Path $project -Leaf) + '.lock.json')) -Force }
    }
    Invoke-Checked $Dotnet @('list', $app, 'package', '--include-transitive', '--no-restore', '--format', 'json') 'dependency-inventory.log'
    Write-Host 'Сборка и автоматические проверки выполнены на этом компьютере.' -ForegroundColor Green
} finally { Pop-Location }
