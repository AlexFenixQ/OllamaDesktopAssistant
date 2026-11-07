# Applies only the bundled signed release; never builds or contacts Ollama.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
& (Join-Path $PSScriptRoot 'scripts\Verify-Package.ps1') -Quiet
$marker=Join-Path $InstallDir 'release-installed.json'
if(-not (Test-Path -LiteralPath $marker)){throw 'Приложение ещё не установлено. Запустите установщик из папки release.'}
$before=Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
if($before.productId -ne 'OllamaDesktopAssistant'){throw 'Неизвестная установка.'}
if($before.version -eq $Version){Write-Host 'Эта версия уже установлена.';return}
if($before.version -ne '0.1.29'){throw 'Этот переход проверен только с версии 0.1.29. Неизвестная база не изменяется.'}
if(Get-Process -Name 'OllamaDesktop' -ErrorAction SilentlyContinue){throw 'Закройте Ollama Desktop и повторите запуск Update.ps1. Незавершённые операции принудительно не останавливаются.'}
$stage=Join-Path $env:LOCALAPPDATA ('OllamaDesktopAssistant\Updates\'+[Guid]::NewGuid().ToString('N'))
New-OwnedDirectory $stage
foreach($name in @('update.json','update.json.sig','installed-files.json')){Copy-Item -LiteralPath (Join-Path $Root ('release\'+$name)) -Destination $stage}
Copy-Item -LiteralPath (Join-Path $Root ('release\OllamaDesktop-'+$Version+'-win-x64-Setup.exe')) -Destination (Join-Path $stage 'Setup.exe')
Copy-Item -LiteralPath (Join-Path $Root 'scripts\Apply-Update.ps1') -Destination $stage
[pscustomobject]@{productId='OllamaDesktopAssistant';installDirectory=$InstallDir;fromVersion=$before.version;channel='stable';pid=2147483640;processStart=0} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'job.json') -Encoding UTF8
$worker=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$p=Start-Process -FilePath $worker -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"'+(Join-Path $stage 'Apply-Update.ps1')+'"'),'-Stage',('"'+$stage+'"')) -WindowStyle Hidden -PassThru
$p.WaitForExit(); $p.Refresh()
if($p.ExitCode -ne 0){throw 'Обновление не завершено. Текущая версия или её резервная копия сохранены. Сведения находятся в локальном last-update.json.'}
Write-Host 'Приложение обновлено и запущено. Резервная копия приложения и данных сохранена локально.'
