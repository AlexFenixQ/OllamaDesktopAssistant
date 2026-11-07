. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Set-BuildEnvironment
& (Join-Path $PSScriptRoot 'Build-Native.ps1')
$project=Join-Path $Root 'tests\OllamaDesktop.Media.Tests\OllamaDesktop.Media.Tests.csproj'
Invoke-Checked $Dotnet @('restore',$project,'-r','win-x64','--locked-mode','--configfile',(Join-Path $Root 'NuGet.Config'))
Invoke-Checked $Dotnet @('build',$project,'-c','Release','-r','win-x64','--no-restore')
$testRoot=Join-Path $Artifacts ('media-test-'+[Guid]::NewGuid().ToString('N'))
New-OwnedDirectory $testRoot
$modelRoot=Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant\Models\Speech'
$env:ODA_SPEECH_MODEL_ROOT=$modelRoot
Write-Host 'Первая проверка автоматически загрузит Whisper Small (~466 МиБ) с проверкой SHA-256. Личных записей и облачных запросов нет.'
$exe=Join-Path $Root 'tests\OllamaDesktop.Media.Tests\bin\Release\net10.0-windows\win-x64\OllamaDesktop.Media.Tests.exe'
Invoke-Checked $exe @((Join-Path $Root 'test_data\Media\conference-video.mp4'),$testRoot,(Join-Path $Artifacts 'media-tests.json')) 'media-tests.log'
Write-Host 'Проверены полный русский текст, отмена, продолжение в новом процессе и кадры видео.'
