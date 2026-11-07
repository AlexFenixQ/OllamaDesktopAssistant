# Source-only transaction helpers. Never traverse .tools, artifacts, application data or unknown directories.
function Get-ManagedPath([string]$Directory, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw 'Небезопасный путь в манифесте.' }
    foreach ($part in $Relative.Replace('\','/').Split('/')) {
        if ($part -in @('', '.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Недопустимый путь в манифесте.' }
    }
    $rootPath = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $path = [IO.Path]::GetFullPath((Join-Path $Directory $Relative))
    if (-not $path.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase) -or $path.Length -ge 260) { throw 'Путь исходного файла небезопасен или слишком длинный.' }
    Assert-PlainPath $path
    return $path
}
function Assert-SourceInventory([string]$Directory, $Files) {
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Files) {
        if (-not $seen.Add($entry.path) -or $entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $entry.bytes -lt 0) { throw 'Некорректный манифест исходников.' }
        $path = Get-ManagedPath $Directory $entry.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Отсутствует ожидаемый файл: ' + $entry.path) }
        if ((Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Исходники изменены. Автоматическая замена запрещена: ' + $entry.path) }
    }
}
function Get-SupportedSourceVersions {
    $match = [regex]::Match($script:Version, '^0\.1\.(\d+)$')
    if (-not $match.Success) { throw 'Неизвестный формат номера версии.' }
    $last = [int]$match.Groups[1].Value
    if ($last -le 0 -or $last -gt 999) { throw 'Недопустимый номер релиза.' }
    for ($n = 0; $n -lt $last; $n++) { '0.1.' + $n }
}
function Get-BaselineFile([string]$FromVersion) {
    $match = [regex]::Match($FromVersion, '^0\.1\.(\d+)$')
    if (-not $match.Success -or $FromVersion -notin @(Get-SupportedSourceVersions)) { throw 'Неизвестная исходная версия.' }
    return ('UPDATE_BASE_01' + $match.Groups[1].Value + '.json')
}
function Get-SourceBaselineFile([string]$FromVersion, [string]$Directory) {
    $name = Get-BaselineFile $FromVersion
    $choices = @($name)
    if ($FromVersion -eq '0.1.28') { $choices += 'UPDATE_BASE_0128_HOTFIX1.json' }
    $manifestPath = Get-ManagedPath $Directory 'MANIFEST.json'
    $actual = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    foreach ($choice in $choices) {
        $baseline = Get-Content -LiteralPath (Join-Path $script:Root $choice) -Raw -Encoding UTF8 | ConvertFrom-Json
        $record = @($baseline.files | Where-Object { $_.path -eq 'MANIFEST.json' })
        if ($record.Count -eq 1 -and $record[0].sha256 -eq $actual) { return $choice }
    }
    throw 'Неизвестный вариант исходной версии. Файлы не изменены.'
}
function Get-CurrentInventory([string]$Directory) {
    $path = Join-Path $Directory 'MANIFEST.json'
    $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.productId -ne 'OllamaDesktopAssistant' -or $manifest.version -ne $script:Version) { throw 'Неверный манифест исправления.' }
    $files = @($manifest.files)
    if (@($files | Where-Object { $_.path -eq 'MANIFEST.json' }).Count -gt 0) { throw 'MANIFEST.json не должен включать собственный SHA-256.' }
    $files += [pscustomobject]@{path='MANIFEST.json'; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    return $files
}
function Assert-NoUnknownCollisions([string]$Directory, $OldFiles, $NewFiles) {
    $owned = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $OldFiles) { if (-not $owned.Add([string]$file.path)) { throw 'Повтор пути в исходном манифесте.' } }
    foreach ($file in $NewFiles) {
        if (-not $owned.Contains([string]$file.path)) {
            $destination = Get-ManagedPath $Directory ([string]$file.path)
            if (Test-Path -LiteralPath $destination) { throw ('Новый путь занят посторонним файлом: ' + $file.path) }
        }
    }
}
function Write-UpdateState([string]$Directory, $State) {
    $path = Join-Path $Directory 'state.json'
    Assert-PlainPath $path
    $temp = Join-Path $Directory ('state-' + [Guid]::NewGuid().ToString('N').Substring(0,8) + '.tmp')
    try {
        $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
        Move-Item -LiteralPath $temp -Destination $path -Force
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function New-SourceBackup([string]$Directory, [string]$Backup, $Files) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Backup) { throw 'Резервная копия уже существует. Перезапись запрещена.' }
    $zip = [IO.Compression.ZipFile]::Open($Backup, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $Files) {
            $source = Get-ManagedPath $Directory $entry.path
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, $entry.path.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
}
function Get-StreamSha256($Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-','') }
    finally { $algorithm.Dispose() }
}
function Assert-BackupArchive([string]$Backup, $Files) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Backup)
    try {
        if ($zip.Entries.Count -ne @($Files).Count) { throw 'Количество файлов резервной копии не совпадает.' }
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) { if (-not $seen.Add($entry.FullName)) { throw 'Повторяющиеся файлы резервной копии.' } }
        foreach ($file in $Files) {
            $entry = $zip.GetEntry($file.path.Replace('\','/'))
            if ($null -eq $entry -or $entry.Length -ne $file.bytes) { throw 'Резервная копия неполна.' }
            $stream = $entry.Open()
            try { if ((Get-StreamSha256 $stream) -ne $file.sha256) { throw ('Повреждена резервная копия: ' + $file.path) } }
            finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function Restore-SourceBackup([string]$Directory, [string]$Backup, $OldFiles, $NewFiles) {
    Assert-BackupArchive $Backup $OldFiles
    # Reject unexpected local edits BEFORE restoring any file.
    $oldMap = @{}; $newMap = @{}
    foreach ($file in $OldFiles) { $oldMap[$file.path] = $file }
    foreach ($file in $NewFiles) { $newMap[$file.path] = $file }
    foreach ($relative in @(@($oldMap.Keys) + @($newMap.Keys) | Select-Object -Unique)) {
        $path = Get-ManagedPath $Directory $relative
        if (Test-Path -LiteralPath $path) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Вместо файла обнаружен каталог: ' + $relative) }
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            $known = ($oldMap.ContainsKey($relative) -and $hash -eq $oldMap[$relative].sha256) -or ($newMap.ContainsKey($relative) -and $hash -eq $newMap[$relative].sha256)
            if (-not $known) { throw ('Откат остановлен: файл изменён после обновления: ' + $relative) }
        }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($Backup)
    try {
        foreach ($file in $OldFiles) {
            $path = Get-ManagedPath $Directory $file.path
            New-OwnedDirectory ([IO.Path]::GetDirectoryName($path))
            $temp = Join-Path ([IO.Path]::GetDirectoryName($path)) ('.r-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
            try {
                $inputStream = $zip.GetEntry($file.path.Replace('\','/')).Open()
                try {
                    $output = [IO.File]::Open($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $inputStream.CopyTo($output) } finally { $output.Dispose() }
                } finally { $inputStream.Dispose() }
                Move-Item -LiteralPath $temp -Destination $path -Force
            } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
        }
    } finally { $zip.Dispose() }
    foreach ($file in $NewFiles) {
        if (-not $oldMap.ContainsKey($file.path)) {
            $path = Get-ManagedPath $Directory $file.path
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
    }
    Assert-SourceInventory $Directory $OldFiles
}

# Transactions have unique names. Do NOT reuse or delete an obsolete 0.1.1
# directory: a previous failed run can have created it before state.json.
# Look only at receipts for THIS target release. Older partial attempts are preserved.
function Get-SourceUpdateAttempts([string]$Directory) {
    $parent = Join-Path $Directory '.updates'
    Assert-PlainPath $parent
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { return }
    $pattern = '^0\.1\.\d+-to-' + [regex]::Escape($script:Version) + '-[0-9a-f]{8}$'
    foreach ($folder in Get-ChildItem -LiteralPath $parent -Directory -Force) {
        if ($folder.Name -cnotmatch $pattern) { continue }
        Assert-PlainPath $folder.FullName
        $record = Join-Path $folder.FullName 'state.json'
        if (-not (Test-Path -LiteralPath $record -PathType Leaf)) {
            # No source file was changed before the receipt was first written.
            continue
        }
        try { $state = Get-Content -LiteralPath $record -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { throw ('Повреждена запись предыдущей попытки ' + $script:Version + '. Данные сохранены.') }
        if ($null -eq $state -or
            $state.productId -ne 'OllamaDesktopAssistant' -or $state.toVersion -ne $script:Version -or
            $state.fromVersion -notin @(Get-SupportedSourceVersions) -or
            $folder.Name -cnotmatch ('^' + [regex]::Escape([string]$state.fromVersion) + '-to-' + [regex]::Escape($script:Version) + '-[0-9a-f]{8}$') -or
            $state.status -notin @('prepared','applying','applied','rolledBack') -or
            $state.backupSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw ('Повреждена запись предыдущей попытки ' + $script:Version + '. Данные сохранены.')
        }
        [pscustomobject]@{ directory=$folder.FullName; state=$state }
    }
}
function New-SourceUpdateAttempt([string]$Directory, [string]$FromVersion) {
    if ($FromVersion -notin @(Get-SupportedSourceVersions)) { throw 'Неизвестная базовая версия обновления.' }
    $parent = Join-Path $Directory '.updates'
    Assert-PlainPath $parent
    foreach ($i in 1..12) {
        $leaf = $FromVersion + '-to-' + $script:Version + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
        $path = Join-Path $parent $leaf
        Assert-PlainPath $path
        if (-not (Test-Path -LiteralPath $path)) { return $path }
    }
    throw 'Не удалось выбрать свободную папку резервирования.'
}
