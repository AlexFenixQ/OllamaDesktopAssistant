. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Assert-InstalledRelease
$answer = Read-Host 'Удалить приложение версии 0.1.31? История, ключ и вложения останутся. Введите ДА'
if ($answer.Trim().ToUpperInvariant() -notin @('ДА','YES')) { Write-Host 'Отменено.'; exit 0 }
$processes = @(Get-Process -Name 'OllamaDesktop' -ErrorAction SilentlyContinue)
if ($processes.Count -gt 0) { throw 'Закройте Ollama Desktop обычным способом и повторите эту команду. Принудительное завершение не используется.' }
$uninstaller = Join-Path $InstallDir 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { throw 'Не найден штатный деинсталлятор. Неизвестные каталоги не удаляются.' }
Start-Process -FilePath $uninstaller -Wait
Write-Host 'Данные не удалялись. Для возврата к предыдущей версии используйте сохранённый проверенный установщик той версии. Данные приложения сохранены.'
