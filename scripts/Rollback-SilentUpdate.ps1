param([string]$Backup)
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$updateHome=Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant'
if(-not $Backup){$last=Get-Content -LiteralPath (Join-Path $updateHome 'last-update.json') -Raw -Encoding UTF8 | ConvertFrom-Json;$Backup=$last.backup}
$Backup=[IO.Path]::GetFullPath($Backup).TrimEnd('\')
$backupRoot=[IO.Path]::GetFullPath((Join-Path $updateHome 'Backups')).TrimEnd('\')+'\'
if(-not $Backup.StartsWith($backupRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Неизвестная резервная копия.'}
function Plain([string]$p){while($p){if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Путь содержит перенаправление.'};$p=[IO.Path]::GetDirectoryName($p)}}
function Child([string]$root,[string]$rel){if([IO.Path]::IsPathRooted($rel) -or $rel.Contains(':') -or ($rel -split '[\\/]' | Where-Object {$_ -in @('..','.','')})){throw 'Небезопасный путь.'};$p=[IO.Path]::GetFullPath((Join-Path $root $rel));if(-not $p.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Небезопасный путь.'};Plain $p;return $p}
Plain $Backup
$receipt=Get-Content -LiteralPath (Join-Path $Backup 'receipt.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$install=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\OllamaDesktopAssistant')).TrimEnd('\');Plain $install
if($receipt.productId -ne 'OllamaDesktopAssistant' -or $receipt.installDirectory -ne $install -or $receipt.state -notin @('installed-and-verified','backup-complete')){throw 'Неверная транзакция.'}
if(Get-Process -Name 'OllamaDesktop' -ErrorAction SilentlyContinue){throw 'Сначала закройте Ollama Desktop. Процесс принудительно не завершается.'}
$old=@(Get-Content -LiteralPath (Join-Path $Backup 'app-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
$new=Get-Content -LiteralPath (Join-Path $Backup 'new-installed-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $Backup 'app.zip'))
foreach($f in $old){$entry=$zip.GetEntry($f.path.Replace('\','/'));if(-not $entry){$zip.Dispose();throw 'Резервная копия повреждена.'};$stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()};if($hash -ne $f.sha256){$zip.Dispose();throw 'Резервная копия повреждена.'}}
foreach($f in Get-ChildItem -LiteralPath $install -File -Recurse -Force){Plain $f.FullName;$rel=$f.FullName.Substring($install.Length).TrimStart('\');$a=@($old | Where-Object path -eq $rel);$b=@($new.files | Where-Object path -eq $rel);$sha=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash;if($rel -notmatch '^unins\d+\.(exe|dat|msg)$' -and $rel -ne 'installed-files.json' -and -not ($a | Where-Object sha256 -eq $sha) -and -not ($b | Where-Object sha256 -eq $sha)){throw ('Неизвестный или изменённый файл: '+$rel)}}
if((Read-Host ('Откатить приложение до '+$receipt.fromVersion+'? История, ключ и новые файлы останутся. Введите ДА')) -cne 'ДА'){throw 'Отмена.'}
foreach($f in $new.files){if(-not ($old | Where-Object path -eq $f.path)){$p=Child $install $f.path;if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force}}}
foreach($f in $old){$p=Child $install $f.path;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)) | Out-Null;[IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($f.path.Replace('\','/')),$p,$true);if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $f.sha256){throw 'Ошибка проверки отката.'}}
$zip.Dispose()
if($receipt.registrySaved){ & reg.exe import (Join-Path $Backup 'uninstall.reg') | Out-Null;if($LASTEXITCODE -ne 0){throw 'Не удалось восстановить регистрацию установщика.'}}
Write-Host 'Приложение восстановлено. Пользовательские данные не изменялись.'
Start-Process -FilePath (Join-Path $install 'OllamaDesktop.exe')
