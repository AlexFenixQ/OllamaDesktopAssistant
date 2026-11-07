. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Set-BuildEnvironment
$testDll = Join-Path $Root 'tests\OllamaDesktop.Tests\bin\Release\net10.0\win-x64\OllamaDesktop.Tests.dll'
if (-not (Test-Path -LiteralPath $testDll)) { throw 'Сначала выполните Start.ps1 или scripts\Build.ps1.' }
Invoke-Checked $Dotnet @($testDll, '--report', (Join-Path $Artifacts 'tests.json')) 'tests.log'
