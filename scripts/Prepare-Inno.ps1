. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
$compiler = Join-Path $Tools 'inno\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    Write-Host 'Для создания Setup.exe нужен Inno Setup 7.1.0. Он будет установлен только для текущего пользователя, в короткую служебную папку ODA.b.'
    Write-Host 'Лицензия и условия коммерческого использования: https://jrsoftware.org/isinfo.php и https://jrsoftware.org/isorder.php'
    $answer = Read-Host 'Загрузить и установить Inno Setup из официального релиза? Введите ДА'
    if ($answer.Trim().ToUpperInvariant() -notin @('ДА','YES')) { throw 'Создание установщика отменено пользователем. Сборка сохранена; исходники менять не нужно.' }
    $installer = Join-Path $Tools 'innosetup-7.1.0-x64.exe'
    Get-OfficialFile 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' $installer
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Подпись издателя Inno Setup не подтверждена. Запуск запрещён.' }
    $arguments = '/CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="' + (Join-Path $Tools 'inno') + '"'
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Установка компилятора Inno Setup не завершена.' }
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Компилятор Inno Setup не найден.' }
# Inno Setup 7.1 provides an explicit --version command; Windows file metadata
# can report a different binary resource version and must not be used for this check.
. (Join-Path $PSScriptRoot 'Inno-Version.ps1')
$reported = & $compiler --version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or -not (Test-InnoCompilerVersion $reported)) {
    throw ('Неожиданная версия Inno Setup. Разрешена только 7.1.0. Вывод компилятора: ' + ($reported.Trim() -replace '[\r\n]+',' '))
}
Write-Host 'Inno Setup готов.' -ForegroundColor Green
