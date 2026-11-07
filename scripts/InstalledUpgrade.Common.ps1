# Safe, versioned in-place Inno Setup upgrade. No user data is stored in InstallDir.
. (Join-Path $PSScriptRoot 'Common.ps1')
Set-StrictMode -Version 2.0
$script:UninstallRegistryKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4959D6E5-8A31-45A2-B215-992E064C87B9}_is1'

function Assert-InstalledAppInventory([string]$Directory, [string[]]$AllowedVersions) {
    Assert-PlainPath $Directory
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'Не найдена известная установленная программа.' }
    $markerPath = Join-Path $Directory 'release-installed.json'
    $inventoryPath = Join-Path $Directory 'installed-files.json'
    foreach ($knownPath in @($markerPath, $inventoryPath)) {
        Assert-PlainPath $knownPath
        if (-not (Test-Path -LiteralPath $knownPath -PathType Leaf)) { throw 'Нет обязательного служебного файла установленной программы.' }
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.productId -ne 'OllamaDesktopAssistant' -or $marker.version -notin $AllowedVersions -or $inventory.version -ne $marker.version) {
        throw 'Неизвестная версия или повреждён манифест установленной программы.'
    }
    $ownedFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [void]$ownedFiles.Add('installed-files.json')
    $ownedDirectories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($inventory.files)) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or
            $relative.StartsWith('\') -or $relative.Replace('\','/').Split('/') -contains '..' -or
            $relative.Replace('\','/').Split('/') -contains '.' -or -not $ownedFiles.Add($relative)) {
            throw 'Недопустимый путь или повторение в манифесте установленной программы.'
        }
        $managed = Join-Path $Directory $relative
        Assert-PlainPath $managed
        if (-not (Test-Path -LiteralPath $managed -PathType Leaf) -or
            (Get-FileHash -LiteralPath $managed -Algorithm SHA256).Hash -ne [string]$entry.sha256 -or
            (Get-Item -LiteralPath $managed).Length -ne [long]$entry.bytes) {
            throw ('Повреждён или изменён установленный файл: ' + $relative)
        }
        $parent = [IO.Path]::GetDirectoryName($relative)
        while ($parent) {
            [void]$ownedDirectories.Add($parent.Replace('/','\'))
            $parent = [IO.Path]::GetDirectoryName($parent)
        }
    }
    if (-not $ownedFiles.Contains('release-installed.json')) { throw 'Метка установки не зарегистрирована в манифесте.' }
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Recurse -Force -File) {
        $relative = $entry.FullName.Substring($Directory.TrimEnd('\').Length).TrimStart('\')
        Assert-PlainPath $entry.FullName
        if (-not $ownedFiles.Contains($relative) -and $relative -notmatch '^unins[0-9]{3}\.(exe|dat|msg)$') {
            throw ('Посторонний файл в каталоге программы: ' + $relative)
        }
    }
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Recurse -Force -Directory) {
        Assert-PlainPath $entry.FullName
        $relative = $entry.FullName.Substring($Directory.TrimEnd('\').Length).TrimStart('\')
        if (-not $ownedDirectories.Contains($relative)) { throw ('Посторонний каталог установленной программы: ' + $relative) }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Directory 'unins000.exe') -PathType Leaf)) {
        throw 'Отсутствует штатный деинсталлятор. Установка будет остановлена.'
    }
    return [pscustomobject]@{version=[string]$marker.version; manifest=$inventory; markerPath=$markerPath; inventoryPath=$inventoryPath}
}

function Assert-InPlaceUpgradeCompatible($OldInventory, $NewInventory) {
    if ($NewInventory.version -ne $script:Version) { throw 'Пакет установки относится к другой версии.' }
    $newFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($NewInventory.files)) {
        $relative = [string]$entry.path
        if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or
            $relative.Replace('\','/').Split('/') -contains '..' -or -not $newFiles.Add($relative)) {
            throw 'Неверный список файлов нового установщика.'
        }
    }
    if (-not $newFiles.Contains('release-installed.json')) { throw 'В новом пакете отсутствует метка версии.' }
    # Retired, hash-verified files are removed only after successful setup and backup.
}
function Remove-ObsoleteInstallFiles([string]$Directory, $OldInventory, $NewInventory) {
    Assert-PlainPath $Directory
    Assert-InPlaceUpgradeCompatible $OldInventory $NewInventory
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $newFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($NewInventory.files)) { [void]$newFiles.Add(([string]$entry.path).Replace('/','\')) }
    $obsolete = @()
    $parents = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($OldInventory.files)) {
        $relative = ([string]$entry.path).Replace('/','\')
        if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Split('\') -contains '..') { throw 'Неверный старый манифест.' }
        if ($newFiles.Contains($relative)) { continue }
        $file = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
        if (-not $file.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Путь удаляемого файла вне установки.' }
        Assert-PlainPath $file
        if (Test-Path -LiteralPath $file) {
            if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne [string]$entry.sha256) {
                throw ('Устаревший файл изменён. Он не удаляется: ' + $relative)
            }
            $obsolete += $file
        }
        $parent = [IO.Path]::GetDirectoryName($file)
        while ($parent -and $parent.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {
            [void]$parents.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent)
        }
    }
    foreach ($file in $obsolete) { Remove-Item -LiteralPath $file -Force }
    foreach ($parent in @($parents | Sort-Object Length -Descending)) {
        Assert-PlainPath $parent
        if ((Test-Path -LiteralPath $parent -PathType Container) -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 0) { Remove-Item -LiteralPath $parent -Force }
    }
}
function Assert-RecoverableInstall([string]$Directory, $OldInventory, $NewInventory, [string]$OldInventoryPath, [string]$NewInventoryPath) {
    Assert-PlainPath $Directory
    $known = @{}
    foreach ($entry in @($OldInventory.files) + @($NewInventory.files)) {
        $relative = ([string]$entry.path).Replace('/','\')
        if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Split('\') -contains '..') { throw 'Небезопасный манифест отката.' }
        if (-not $known.ContainsKey($relative)) { $known[$relative] = @() }
        $known[$relative] += [string]$entry.sha256
    }
    $known['installed-files.json'] = @((Get-FileHash -LiteralPath $OldInventoryPath -Algorithm SHA256).Hash, (Get-FileHash -LiteralPath $NewInventoryPath -Algorithm SHA256).Hash)
    $directories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in @($known.Keys)) {
        $parent = [IO.Path]::GetDirectoryName($relative)
        while ($parent) { [void]$directories.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
    }
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        Assert-PlainPath $item.FullName
        $relative = $item.FullName.Substring($Directory.TrimEnd('\').Length).TrimStart('\')
        if ($item.PSIsContainer) {
            if (-not $directories.Contains($relative)) { throw ('Посторонний каталог: ' + $relative) }
        } elseif ($relative -notmatch '^unins[0-9]{3}\.(exe|dat|msg)$') {
            if (-not $known.ContainsKey($relative) -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -notin $known[$relative]) {
                throw ('Неизвестный или изменённый файл: ' + $relative)
            }
        }
    }
}

function Assert-InstallBackup([string]$ArchivePath, $OldInventory) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) { throw 'Нет архива резервного копирования приложения.' }
    $checked = Get-SafeZipPlan $ArchivePath (Join-Path $Tools ('verify-' + [Guid]::NewGuid().ToString('N').Substring(0,8)))
    if ($checked.entries -lt @($OldInventory.files).Count) { throw 'В резервном архиве отсутствуют файлы.' }
    $z = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in @($OldInventory.files)) {
            $name = ([string]$entry.path).Replace('\','/')
            $member = @($z.Entries | Where-Object { $_.FullName -ieq $name })
            if ($member.Count -ne 1) { throw ('В резервном архиве отсутствует ' + $name) }
            $input = $member[0].Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($input)).Replace('-','') }
            finally { $hasher.Dispose(); $input.Dispose() }
            if ($actualHash -ne [string]$entry.sha256 -or $member[0].Length -ne [long]$entry.bytes) {
                throw ('Резервная копия повреждена: ' + $name)
            }
        }
    } finally { $z.Dispose() }
}

function New-VerifiedInstallBackup([string]$Directory, [string]$ArchivePath, $OldInventory) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $ArchivePath) { throw 'Резервный архив уже существует.' }
    Assert-PlainPath $Directory
    Assert-PlainPath $ArchivePath
    $sourceRoot = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $sourcePrefix = $sourceRoot + '\'
    $archiveStream = $null
    $archive = $null
    try {
        $archiveStream = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $archive = New-Object IO.Compression.ZipArchive($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($file in @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force -File | Sort-Object FullName)) {
            Assert-PlainPath $file.FullName
            $fullName = [IO.Path]::GetFullPath($file.FullName)
            if (-not $fullName.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Путь резервируемого файла вне каталога программы.' }
            $relative = $fullName.Substring($sourcePrefix.Length).Replace('\','/')
            if (-not $relative -or $relative.StartsWith('/') -or $relative.Contains(':') -or $relative.Split('/') -contains '..') { throw 'Неверное имя в резервном архиве.' }
            $member = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $inputStream = [IO.File]::OpenRead($fullName)
            $outputStream = $member.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally {
        if ($null -ne $archive) { $archive.Dispose() }
        if ($null -ne $archiveStream) { $archiveStream.Dispose() }
    }
    Assert-InstallBackup $ArchivePath $OldInventory
    return (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
}

function New-UpgradePermit([string]$Directory, [string]$ProductVersion) {
    if ($ProductVersion -ne $script:Version) { throw 'Версия разрешения не соответствует пакету.' }
    $token = [Guid]::NewGuid().ToString('N')
    $folder = Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant\UpgradePermits'
    New-OwnedDirectory $folder
    $path = Join-Path $folder ($token + '.txt')
    if (Test-Path -LiteralPath $path) { throw 'Разрешение обновления уже существует.' }
    $markerHash = (Get-FileHash -LiteralPath (Join-Path $Directory 'release-installed.json') -Algorithm SHA256).Hash
    $inventoryHash = (Get-FileHash -LiteralPath (Join-Path $Directory 'installed-files.json') -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($path, ((@('OllamaDesktopAssistant', $script:Version, $token, $markerHash, $inventoryHash)) -join "`r`n"), [Text.Encoding]::ASCII)
    return [pscustomobject]@{token=$token;path=$path;markerHash=$markerHash;inventoryHash=$inventoryHash}
}
