param([switch]$NoBuild)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'SourceUpdate.Common.ps1')
$ErrorActionPreference = 'Stop'
Assert-Environment
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Quiet
# All preparation tests run BEFORE touching the owner's versioned source directories.
& (Join-Path $PSScriptRoot 'Test-Bootstrap.ps1')

$source = $Root
$targetParent = Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant\Source'
$target = Join-Path $targetParent $Version
Assert-PlainPath $target
Assert-PlainPath $targetParent
$newFiles = @(Get-CurrentInventory $source)
if (Test-Path -LiteralPath $target) {
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'Путь новой версии занят файлом; замена запрещена.' }
    Assert-SourceInventory $target $newFiles
    Write-Host ('Рабочая копия ' + $Version + ' уже проверена; повторная распаковка не нужна.') -ForegroundColor Green
} else {
    New-OwnedDirectory $targetParent
    $staging = Join-Path $targetParent ('.staging-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    if (Test-Path -LiteralPath $staging) { throw 'Временная папка занята.' }
    New-OwnedDirectory $staging
    try {
        foreach ($entry in $newFiles) {
            $original = Get-ManagedPath $source $entry.path
            $copy = Get-ManagedPath $staging $entry.path
            New-OwnedDirectory ([IO.Path]::GetDirectoryName($copy))
            [IO.File]::Copy($original, $copy, $false)
        }
        Assert-SourceInventory $staging $newFiles
        if (Test-Path -LiteralPath $target) { throw 'Путь рабочей копии был занят в ходе операции.' }
        Move-Item -LiteralPath $staging -Destination $target
    } finally {
        # Leave any failed staging folder untouched for diagnosis, never delete unverified content.
        if (Test-Path -LiteralPath $staging) { Write-Warning ('Служебная папка не завершена: ' + $staging) }
    }
    Assert-SourceInventory $target $newFiles
    Write-Host ('Создана проверенная рабочая копия ' + $Version + ' без изменения прежних исходников.') -ForegroundColor Green
}
if ($NoBuild) {
    Write-Host ('Подготовка завершена: ' + $target)
    return
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $target 'Start.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Сборка или установка остановлена. Диагностика находится в artifacts новой рабочей копии.' }
