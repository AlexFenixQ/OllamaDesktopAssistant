# Executed on Windows BEFORE downloading/starting the SDK. No API keys or external packages.
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'SourceUpdate.Common.ps1')
. (Join-Path $PSScriptRoot 'InstalledUpgrade.Common.ps1')
Assert-Environment
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixture = Join-Path $env:LOCALAPPDATA ('ODA.t-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
if (Test-Path -LiteralPath $fixture) { throw 'Тестовая папка уже существует.' }
New-OwnedDirectory $fixture
$results = New-Object 'System.Collections.Generic.List[object]'
function Invoke-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $results.Add([pscustomobject]@{name=$Name; passed=$true; detail=''}) }
    catch { $results.Add([pscustomobject]@{name=$Name; passed=$false; detail=(Protect-DiagnosticText $_.Exception.Message)}) }
}
function Expect-Rejection([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; return }
    throw 'Ожидаемый запрет не сработал.'
}
function Write-TestZip([string]$Name, [string[]]$Entries) {
    $path = Join-Path $fixture $Name
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $Entries) {
            $entry = $zip.CreateEntry($entryName)
            if (-not $entryName.EndsWith('/')) {
                $stream = $entry.Open()
                try { $bytes = [Text.Encoding]::UTF8.GetBytes('test data'); $stream.Write($bytes,0,$bytes.Length) }
                finally { $stream.Dispose() }
            }
        }
    } finally { $zip.Dispose() }
    return $path
}
function Get-TestFileRecord([string]$Directory, [string]$Relative) {
    $path = Join-Path $Directory $Relative
    return [pscustomobject]@{path=$Relative; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
}
try {
    Invoke-Case 'ZIP: valid Unicode and spaces' {
        $zip = Write-TestZip 'unicode.zip' @('Папка с пробелами/файл.txt','empty/')
        $dest = Join-Path $fixture 'unicode'
        Expand-SafeZip $zip $dest
        if ([IO.File]::ReadAllText((Join-Path $dest 'Папка с пробелами\файл.txt')) -ne 'test data') { throw 'Содержимое не совпало.' }
    }
    Invoke-Case 'ZIP: existing target is preserved' {
        $zip = Write-TestZip 'existing.zip' @('file.txt')
        $dest = Join-Path $fixture 'existing'; New-OwnedDirectory $dest
        Expect-Rejection { Expand-SafeZip $zip $dest } 'уже существует'
    }
    foreach ($sample in @(
        @('traversal','../outside.txt'), @('absolute','C:/outside.txt'), @('unc','//server/share/file.txt'),
        @('ads','file.txt:stream'), @('reserved','CON.txt'), @('dotsegment','dir/./file.txt'),
        @('trailingdot','dir./file.txt'), @('trailingspace','dir /file.txt'), @('emptysegment','dir//file.txt')
    )) {
        $caseName = $sample[0]; $entryName = $sample[1]
        Invoke-Case ('ZIP: rejects ' + $caseName) {
            $zip = Write-TestZip ($caseName + '.zip') @($entryName)
            $dest = Join-Path $fixture $caseName
            Expect-Rejection { Expand-SafeZip $zip $dest } 'Небезопасн'
            if (Test-Path -LiteralPath $dest) { throw 'Распаковка началась до проверки.' }
        }
    }
    Invoke-Case 'ZIP: case-insensitive duplicate' {
        $zip = Write-TestZip 'duplicate.zip' @('a.txt','A.txt')
        Expect-Rejection { Get-SafeZipPlan $zip (Join-Path $fixture 'dupe') } 'Повторяющиеся'
    }
    Invoke-Case 'ZIP: file/directory collision' {
        $zip = Write-TestZip 'collision.zip' @('same','same/file.txt')
        Expect-Rejection { Get-SafeZipPlan $zip (Join-Path $fixture 'collision') } 'Конфликт'
    }
    Invoke-Case 'ZIP: rejects 260-character path BEFORE extraction' {
        $dest = Join-Path $fixture 'long'
        $name = ('a' * (260 - $dest.Length - 1 - 4)) + '.txt'
        $zip = Write-TestZip 'long.zip' @($name)
        Expect-Rejection { Expand-SafeZip $zip $dest } 'ДО записи'
        if (Test-Path -LiteralPath $dest) { throw 'Распаковка началась до проверки длины.' }
    }
    Invoke-Case 'SDK: original regression fits the new cache path' {
        $name = 'sdk/10.0.402/DotnetTools/dotnet-user-jwts/10.0.13-servicing.26504.104/tools/net10.0/any/Microsoft.Extensions.DependencyInjection.Abstractions.dll'
        $zip = Write-TestZip 'sdk-path.zip' @($name)
        $plan = Get-SafeZipPlan $zip (Join-Path $Tools 's-12345678')
        if ($plan.maxPathLength -ge 260) { throw 'Новый путь не устранил ограничение.' }
    }
    Invoke-Case 'Source update: refuses path traversal' {
        Expect-Rejection { Get-ManagedPath $fixture '../outside.txt' } 'Недопустим'
    }
    Invoke-Case 'Source update: verifies original, backup and rollback; unrelated file preserved' {
        $source = Join-Path $fixture 'source'; New-OwnedDirectory $source
        [IO.File]::WriteAllText((Join-Path $source 'known.txt'),'original')
        [IO.File]::WriteAllText((Join-Path $source 'unrelated.txt'),'keep me')
        $old = @(Get-TestFileRecord $source 'known.txt')
        Assert-SourceInventory $source $old
        $backup = Join-Path $fixture 'source-backup.zip'
        New-SourceBackup $source $backup $old; Assert-BackupArchive $backup $old
        [IO.File]::WriteAllText((Join-Path $source 'known.txt'),'new version')
        [IO.File]::WriteAllText((Join-Path $source 'added.txt'),'new file')
        $new = @((Get-TestFileRecord $source 'known.txt'),(Get-TestFileRecord $source 'added.txt'))
        Restore-SourceBackup $source $backup $old $new
        if ([IO.File]::ReadAllText((Join-Path $source 'known.txt')) -ne 'original') { throw 'Исходный файл не восстановлен.' }
        if (Test-Path -LiteralPath (Join-Path $source 'added.txt')) { throw 'Добавленный файл не удалён.' }
        if ([IO.File]::ReadAllText((Join-Path $source 'unrelated.txt')) -ne 'keep me') { throw 'Посторонний файл изменён.' }
    }
    Invoke-Case 'Source update: refuses modified baseline' {
        $source = Join-Path $fixture 'edited'; New-OwnedDirectory $source
        [IO.File]::WriteAllText((Join-Path $source 'known.txt'),'original')
        $old = @(Get-TestFileRecord $source 'known.txt')
        [IO.File]::WriteAllText((Join-Path $source 'known.txt'),'edited')
        Expect-Rejection { Assert-SourceInventory $source $old } 'Исходники изменены'
    }
    Invoke-Case 'Source rollback: refuses unexpected edits without overwriting them' {
        $source = Join-Path $fixture 'rollback-edited'; New-OwnedDirectory $source
        [IO.File]::WriteAllText((Join-Path $source 'known.txt'),'original')
        $old = @(Get-TestFileRecord $source 'known.txt')
        $backup = Join-Path $fixture 'rollback-edited.zip'; New-SourceBackup $source $backup $old
        [IO.File]::WriteAllText((Join-Path $source 'known.txt'),'updated')
        $new = @(Get-TestFileRecord $source 'known.txt')
        [IO.File]::WriteAllText((Join-Path $source 'known.txt'),'unexpected edit')
        Expect-Rejection { Restore-SourceBackup $source $backup $old $new } 'изменён после обновления'
        if ([IO.File]::ReadAllText((Join-Path $source 'known.txt')) -ne 'unexpected edit') { throw 'Неизвестные изменения потеряны.' }
    }
    Invoke-Case 'Source update: orphan old attempt without state.json is preserved' {
        $source = Join-Path $fixture 'orphan-source'; New-OwnedDirectory $source
        $orphan = Join-Path $source '.updates\0.1.0-to-0.1.1'
        New-OwnedDirectory $orphan
        [IO.File]::WriteAllText((Join-Path $orphan 'source-0.1.0.zip'),'old interrupted backup')
        $attempts = @(Get-SourceUpdateAttempts $source)
        if ($attempts.Count -ne 0) { throw 'Старая папка ошибочно принята за активную.' }
        $newAttempt = New-SourceUpdateAttempt $source '0.1.0'
        if ($newAttempt -eq $orphan -or (Test-Path -LiteralPath $newAttempt)) { throw 'Не выбрана новая свободная транзакция.' }
        if ([IO.File]::ReadAllText((Join-Path $orphan 'source-0.1.0.zip')) -ne 'old interrupted backup') { throw 'Старая папка изменена.' }
    }
    Invoke-Case 'Source update: active transaction is discovered from valid receipt' {
        $source = Join-Path $fixture 'active-source'; New-OwnedDirectory $source
        $attempt = New-SourceUpdateAttempt $source '0.1.1'; New-OwnedDirectory $attempt
        $state = [pscustomobject]@{productId='OllamaDesktopAssistant';fromVersion='0.1.1';toVersion=$Version;status='applying';backupSha256=('A'*64);newFiles=@()}
        Write-UpdateState $attempt $state
        $records = @(Get-SourceUpdateAttempts $source)
        if ($records.Count -ne 1 -or $records[0].state.status -ne 'applying') { throw 'Транзакция не обнаружена.' }
    }
    Invoke-Case 'Source update: historical 0.1.6 transaction is not misclassified' {
        $src = Join-Path $fixture 'older-attempt'; New-OwnedDirectory $src
        $previous = Join-Path $src '.updates\0.1.5-to-0.1.6-12345678'; New-OwnedDirectory $previous
        $oldState = [pscustomobject]@{productId='OllamaDesktopAssistant';fromVersion='0.1.5';toVersion='0.1.6';status='applied';backupSha256=('A'*64)}
        Write-UpdateState $previous $oldState
        if (@(Get-SourceUpdateAttempts $src).Count -ne 0) { throw 'Историческая транзакция ошибочно принята за текущую.' }
        if (-not (Test-Path -LiteralPath (Join-Path $previous 'state.json'))) { throw 'Историческая запись повреждена.' }
    }
    Invoke-Case 'Source update: current receipt recognized' {
        $src = Join-Path $fixture 'current-attempt'; New-OwnedDirectory $src
        $folder = Join-Path $src ('.updates\0.1.7-to-' + $Version + '-12345678'); New-OwnedDirectory $folder
        $state = [pscustomobject]@{productId='OllamaDesktopAssistant';fromVersion='0.1.7';toVersion=$Version;status='applied';backupSha256=('A'*64)}
        Write-UpdateState $folder $state
        $found = @(Get-SourceUpdateAttempts $src)
        if ($found.Count -ne 1 -or $found[0].state.toVersion -ne $Version) { throw 'Запись текущей версии не обнаружена.' }
    }
    Invoke-Case 'Inno: canonical version parser' {
        . (Join-Path $PSScriptRoot 'Inno-Version.ps1')
        if (-not (Test-InnoCompilerVersion 'Inno Setup 7.1.0')) { throw 'Expected 7.1.0 rejected' }
        if (-not (Test-InnoCompilerVersion 'Inno Setup Compiler version 7.1.0')) { throw 'Expected compiler banner rejected' }
        if (Test-InnoCompilerVersion 'Inno Setup 7.0.2') { throw 'Wrong version accepted' }
        if (Test-InnoCompilerVersion 'Inno Setup 7.1.1') { throw 'Newer version accepted' }
    }
    Invoke-Case 'Source update: unexpected transaction marker is rejected' {
        $source = Join-Path $fixture 'bad-state-source'; New-OwnedDirectory $source
        $attempt = New-SourceUpdateAttempt $source '0.1.0'; New-OwnedDirectory $attempt
        $state = [pscustomobject]@{productId='unknown';fromVersion='0.1.0';toVersion=$Version;status='applying';backupSha256=('A'*64)}
        Write-UpdateState $attempt $state
        Expect-Rejection { @(Get-SourceUpdateAttempts $source) | Out-Null } 'Повреждена запись'
    }
    Invoke-Case 'PowerShell 5.1: every script uses UTF-8 BOM and is syntactically valid' {
        $sourceManifest = Get-Content -LiteralPath (Join-Path $Root 'MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($scriptRecord in @($sourceManifest.files | Where-Object { $_.path.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase) })) {
            $scriptFile = Get-Item -LiteralPath (Join-Path $Root $scriptRecord.path)
            $raw = [IO.File]::ReadAllBytes($scriptFile.FullName)
            if ($raw.Length -lt 3 -or $raw[0] -ne 239 -or $raw[1] -ne 187 -or $raw[2] -ne 191) {
                throw ('UTF-8 BOM missing: ' + $scriptFile.FullName)
            }
            $tokens = $null; $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$errors) | Out-Null
            if (@($errors).Count -gt 0) { throw ('PowerShell parsing failure: ' + $scriptFile.FullName) }
        }
    }
    Invoke-Case 'Source update: fixture paths use current release version' {
        foreach ($from in @('0.1.9','0.1.10','0.1.11')) {
            $source = Join-Path $fixture ('version-' + $from); New-OwnedDirectory $source
            $attempt = New-SourceUpdateAttempt $source $from
            $expectedSuffix = [regex]::Escape($from + '-to-' + $Version + '-') + '[0-9a-f]{8}$'
            if ($attempt -cnotmatch $expectedSuffix) { throw ('Incorrect release transaction name for ' + $from) }
        }
    }
    Invoke-Case 'Source update: 0.1.10 baseline recognized' {
        $source = Join-Path $fixture 'base-010'; New-OwnedDirectory $source
        $next = New-SourceUpdateAttempt $source '0.1.10'
        if ($next -cnotmatch ('0\.1\.10-to-' + [regex]::Escape($Version) + '-[0-9a-f]{8}$')) { throw 'Incorrect baseline' }
    }
    Invoke-Case 'Source update: 0.1.9 baseline recognized' {
        $source = Join-Path $fixture 'latest-baseline'; New-OwnedDirectory $source
        $next = New-SourceUpdateAttempt $source '0.1.9'
        if ($next -cnotmatch ('0\.1\.9-to-' + [regex]::Escape($Version) + '-[0-9a-f]{8}$')) { throw 'Incorrect version transaction' }
    }
    Invoke-Case 'Release contract: test status defines expectedTotalWindowsTests' {
        $status = Get-Content -LiteralPath (Join-Path $Root 'TEST_STATUS.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $status.PSObject.Properties['expectedTotalWindowsTests']) { throw 'Missing expectedTotalWindowsTests' }
        if ([int]$status.expectedTotalWindowsTests -ne (Get-ReleaseTestCount)) { throw 'Wrong test count' }
    }
    Invoke-Case 'Release: test count matches declared contract before build' {
        $expectedCount = [int](Get-ReleaseTestCount)
        if ((Assert-ReleaseContracts) -ne $expectedCount -or $expectedCount -lt 60) { throw 'Test count contract mismatch' }
    }
    Invoke-Case 'Test report: matching count accepted, missing tests rejected' {
        $path = Join-Path $fixture 'test-report-fixture.json'
        $expectedCount = [int](Get-ReleaseTestCount)
        $entries = @(1..$expectedCount | ForEach-Object { [pscustomobject]@{name=('case-' + $_);passed=$true} })
        [pscustomobject]@{version=$Version;count=$expectedCount;failed=0;tests=$entries} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
        $valid = Assert-TestReport $path
        if ($valid.count -ne $expectedCount) { throw 'Valid report rejected' }
        [pscustomobject]@{version=$Version;count=($expectedCount - 1);failed=0;tests=@($entries | Select-Object -First ($expectedCount - 1))} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
        Expect-Rejection { $null = Assert-TestReport $path } 'Отчёт тестов'
    }
    Invoke-Case 'Test report: wrong version and duplicate cases rejected' {
        $path = Join-Path $fixture 'test-report-version.json'
        $expectedCount = [int](Get-ReleaseTestCount)
        $entries = @(1..$expectedCount | ForEach-Object { [pscustomobject]@{name=('case-' + $_);passed=$true} })
        [pscustomobject]@{version='0.1.11';count=$expectedCount;failed=0;tests=$entries} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
        Expect-Rejection { $null = Assert-TestReport $path } 'Отчёт тестов'
        $entries[1].name = $entries[0].name
        [pscustomobject]@{version=$Version;count=$expectedCount;failed=0;tests=$entries} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
        Expect-Rejection { $null = Assert-TestReport $path } 'повторяющиеся'
    }
    Invoke-Case 'Source update: 0.1.11 predecessor transaction is recognized' {
        $source = Join-Path $fixture 'last-release-baseline'; New-OwnedDirectory $source
        $attempt = New-SourceUpdateAttempt $source '0.1.11'
        if ($attempt -cnotmatch ('0\.1\.11-to-' + [regex]::Escape($Version) + '-[0-9a-f]{8}$')) { throw 'Transaction for 0.1.11 is not supported' }
        New-OwnedDirectory $attempt
        $state = [pscustomobject]@{productId='OllamaDesktopAssistant';fromVersion='0.1.11';toVersion=$Version;status='prepared';backupSha256=('A'*64)}
        Write-UpdateState $attempt $state
        if (@(Get-SourceUpdateAttempts $source).Count -ne 1) { throw 'Update attempt for 0.1.11 not detected' }
    }
    Invoke-Case 'Source update: attempts match current release' {
        $source = Join-Path $fixture 'attempts-current'; New-OwnedDirectory $source
        $attempt = New-SourceUpdateAttempt $source '0.1.17'
        if ($attempt -cnotmatch ('0\.1\.17-to-' + [regex]::Escape($Version) + '-[0-9a-f]{8}$')) { throw 'Wrong folder version' }
        New-OwnedDirectory $attempt
        $state = [pscustomobject]@{ productId='OllamaDesktopAssistant';fromVersion='0.1.17';toVersion=$Version;status='prepared';backupSha256=('A'*64) }
        Write-UpdateState $attempt $state
        if (@(Get-SourceUpdateAttempts $source).Count -ne 1) { throw 'Receipt not found' }
        $state.toVersion = '0.1.16'; Write-UpdateState $attempt $state
        $refused = $false
        try { @(Get-SourceUpdateAttempts $source) | Out-Null } catch { $refused = $true }
        if (-not $refused) { throw 'Corrupt receipt accepted' }
    }
    Invoke-Case 'Source update: every predecessor has a verified MANIFEST record' {
        # PowerShell variables are case-insensitive. Do NOT use $version as a loop
        # variable: it aliases $Version and overwrites the current release number.
        $currentRelease = [string]$script:Version
        $priorReleases = @(Get-SupportedSourceVersions)
        $releaseNumber = [int]([regex]::Match($currentRelease, '^0\.1\.(\d+)$').Groups[1].Value)
        if ($priorReleases.Count -ne $releaseNumber) { throw 'Historical version inventory is incomplete' }
        foreach ($priorRelease in $priorReleases) {
            $file = Get-BaselineFile $priorRelease
            $baseline = Get-Content -LiteralPath (Join-Path $Root $file) -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($baseline.productId -ne 'OllamaDesktopAssistant' -or $baseline.fromVersion -ne $priorRelease -or $baseline.toVersion -ne $currentRelease) {
                throw ('Wrong baseline: ' + $file)
            }
            $record = @($baseline.files | Where-Object { $_.path -eq 'MANIFEST.json' })
            if ($record.Count -ne 1 -or $record[0].sha256 -notmatch '^[A-F0-9]{64}$' -or $record[0].bytes -lt 100) {
                throw ('Untracked MANIFEST.json in ' + $file)
            }
        }
        if ($script:Version -cne $currentRelease) { throw 'Preflight mutated the release version' }
    }
    Invoke-Case 'Source update: historical inspection cannot change release context' {
        $expectedRelease = [string]$script:Version
        foreach ($priorRelease in @(Get-SupportedSourceVersions)) {
            if ($priorRelease -eq $expectedRelease) { throw 'Current version included as predecessor' }
        }
        if ($script:Version -cne $expectedRelease) { throw 'Current release was overwritten by a local loop variable' }
        $null = Assert-ReleaseContracts
    }
    Invoke-Case 'Source update: known MANIFEST can be replaced' {
        $src = Join-Path $fixture 'owned-manifest'; New-OwnedDirectory $src
        [IO.File]::WriteAllText((Join-Path $src 'MANIFEST.json'), 'old')
        $old = @(Get-TestFileRecord $src 'MANIFEST.json')
        $new = @([pscustomobject]@{ path='MANIFEST.json'; bytes=3; sha256=('B'*64) })
        Assert-SourceInventory $src $old
        Assert-NoUnknownCollisions $src $old $new
    }
    Invoke-Case 'Source update: untracked MANIFEST cannot be overwritten' {
        $src = Join-Path $fixture 'foreign-manifest'; New-OwnedDirectory $src
        [IO.File]::WriteAllText((Join-Path $src 'MANIFEST.json'), 'unrelated')
        $new = @([pscustomobject]@{ path='MANIFEST.json'; bytes=1; sha256=('B'*64) })
        Expect-Rejection { Assert-NoUnknownCollisions -Directory $src -OldFiles @() -NewFiles $new } 'посторонним'
        if ([IO.File]::ReadAllText((Join-Path $src 'MANIFEST.json')) -ne 'unrelated') { throw 'Unknown data was overwritten' }
    }
    Invoke-Case 'Source update: last known version creates correct receipt path' {
        $src = Join-Path $fixture 'prev-current'; New-OwnedDirectory $src
        $previous = '0.1.' + ([int]([regex]::Match($Version,'^0\.1\.(\d+)$').Groups[1].Value) - 1)
        $attempt = New-SourceUpdateAttempt $src $previous
        if ($attempt -cnotmatch ([regex]::Escape($previous + '-to-' + $Version + '-') + '[0-9a-f]{8}$')) { throw 'Last release not supported' }
    }
    Invoke-Case 'Inno upgrade: stable AppId and strictly gated in-place upgrade' {
        $scriptText = Get-Content -LiteralPath (Join-Path $Root 'deployment\OllamaDesktop.iss') -Raw -Encoding UTF8
        if ($scriptText -notmatch 'AppId=\{\{4959D6E5-8A31-45A2-B215-992E064C87B9\}' -or
            $scriptText -notmatch 'HasVerifiedUpgradePermit' -or
            $scriptText -notmatch '\{param:ODAVERIFY\|\}' -or
            $scriptText -notmatch 'GetSHA256OfFile\(') { throw 'Installer does not gate in-place upgrades' }
        $updater = Get-Content -LiteralPath (Join-Path $Root 'scripts\Update-Installed.ps1') -Raw -Encoding UTF8
        if ($updater -match 'Start-Process\s+-FilePath\s+\$uninstaller' -or
            $updater -notmatch 'Start-Process\s+-FilePath\s+\$Setup' -or
            $updater -notmatch 'Assert-InstalledAppInventory') { throw 'Update must not run the previous uninstaller' }
    }
    Invoke-Case 'Upgrade: verified manifest permits known files only' {
        $directory = Join-Path $fixture 'upgrade-owned'; New-OwnedDirectory $directory
        [IO.File]::WriteAllText((Join-Path $directory 'good.txt'), 'safe content')
        [IO.File]::WriteAllText((Join-Path $directory 'unins000.exe'), 'dummy uninstaller')
        [IO.File]::WriteAllText((Join-Path $directory 'unins000.dat'), 'dummy log')
        [pscustomobject]@{productId='OllamaDesktopAssistant';version='0.1.20'} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $directory 'release-installed.json') -Encoding UTF8
        $records = @((Get-TestFileRecord $directory 'good.txt'), (Get-TestFileRecord $directory 'release-installed.json'))
        [pscustomobject]@{version='0.1.20';files=$records} | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $directory 'installed-files.json') -Encoding UTF8
        $state = Assert-InstalledAppInventory $directory @('0.1.20')
        if ($state.version -ne '0.1.20') { throw 'Known version refused' }
        [IO.File]::WriteAllText((Join-Path $directory 'foreign.txt'), 'must remain')
        Expect-Rejection { $null = Assert-InstalledAppInventory $directory @('0.1.20') } 'Посторонний файл'
        if ([IO.File]::ReadAllText((Join-Path $directory 'foreign.txt')) -ne 'must remain') { throw 'Foreign file was modified' }
    }
    Invoke-Case 'Upgrade: archive backup checks actual file hashes' {
        $directory = Join-Path $fixture 'upgrade-backup'; New-OwnedDirectory $directory
        [IO.File]::WriteAllText((Join-Path $directory 'program.txt'), 'existing application')
        [IO.File]::WriteAllText((Join-Path $directory 'unins000.exe'), 'dummy')
        [pscustomobject]@{productId='OllamaDesktopAssistant';version='0.1.20'} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $directory 'release-installed.json') -Encoding UTF8
        $records = @((Get-TestFileRecord $directory 'program.txt'),(Get-TestFileRecord $directory 'release-installed.json'))
        [pscustomobject]@{version='0.1.20';files=$records} | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $directory 'installed-files.json') -Encoding UTF8
        $state = Assert-InstalledAppInventory $directory @('0.1.20')
        $backup = Join-Path $fixture 'upgrade-verified.zip'
        $hash = New-VerifiedInstallBackup $directory $backup $state.manifest
        if ($hash -ne (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash) { throw 'Backup hash differs' }
        $newRecords = @($records)
        Assert-InPlaceUpgradeCompatible $state.manifest ([pscustomobject]@{version=$Version;files=$newRecords})
        $less = @($records | Select-Object -First 1)
        Expect-Rejection { Assert-InPlaceUpgradeCompatible $state.manifest ([pscustomobject]@{version=$Version;files=$less}) } 'метка версии'
    }
    Invoke-Case 'Upgrade: nested localized DLL backup paths' {
        $installed = Join-Path $fixture 'localized-install'
        New-OwnedDirectory (Join-Path $installed 'cs')
        [IO.File]::WriteAllText((Join-Path $installed 'cs\PresentationCore.resources.dll'),'fixture-locale')
        [IO.File]::WriteAllText((Join-Path $installed 'release-installed.json'),'fixture-marker')
        $tracked = @(
            (Get-TestFileRecord $installed 'cs\PresentationCore.resources.dll'),
            (Get-TestFileRecord $installed 'release-installed.json')
        )
        $backupArchive = Join-Path $fixture 'localized-install-backup.zip'
        $null = New-VerifiedInstallBackup $installed $backupArchive ([pscustomobject]@{files=$tracked})
        Assert-InstallBackup $backupArchive ([pscustomobject]@{files=$tracked})
        $zip = [IO.Compression.ZipFile]::OpenRead($backupArchive)
        try {
            if (@($zip.Entries | Where-Object { $_.FullName -ceq 'cs/PresentationCore.resources.dll' }).Count -ne 1) {
                throw 'Нет корректной ZIP-записи для вложенного каталога локализации.'
            }
        } finally { $zip.Dispose() }
    }
    Invoke-Case 'Upgrade: permit binds both tracked manifests to release' {
        $directory = Join-Path $fixture 'permit-fixture'; New-OwnedDirectory $directory
        [IO.File]::WriteAllText((Join-Path $directory 'release-installed.json'), 'marker')
        [IO.File]::WriteAllText((Join-Path $directory 'installed-files.json'), 'manifest')
        $permit = New-UpgradePermit $directory $Version
        try {
            $record = [IO.File]::ReadAllLines($permit.path,[Text.Encoding]::ASCII)
            if ($record.Length -ne 5 -or $record[1] -ne $Version -or $record[2] -ne $permit.token -or
                $record[3] -ne $permit.markerHash -or $record[4] -ne $permit.inventoryHash) { throw 'Upgrade permit is inconsistent' }
        } finally { if (Test-Path -LiteralPath $permit.path) { Remove-Item -LiteralPath $permit.path -Force } }
    }

    Invoke-Case 'Compatibility: Windows 10 setup minimum is 10240' {
        $setupText = Get-Content -LiteralPath (Join-Path $Root 'deployment\OllamaDesktop.iss') -Raw -Encoding UTF8
        if ($setupText -notmatch '(?m)^MinVersion=10\.0\.10240\r?$') { throw 'Windows 10 1507 installer gate missing' }
        if ($setupText -notmatch '(?m)^ArchitecturesAllowed=x64compatible\r?$') { throw 'Win11 ARM64 x64 emulator allowance missing' }
    }
    Invoke-Case 'Compatibility: WPF startup does not require Windows 11' {
        $source = Get-Content -LiteralPath (Join-Path $Root 'src\OllamaDesktop.App\App.xaml.cs') -Raw -Encoding UTF8
        if ($source -notmatch 'IsWindowsVersionAtLeast\(10, 0, 10240\)' -or $source -match 'IsWindowsVersionAtLeast\(10, 0, 22000\)\s*\|\|') { throw 'WPF runtime gate still requires Windows 11' }
    }
    Invoke-Case 'Compatibility: owner bootstrap accepts Win10 while retaining CPU checks' {
        $commonSource = Get-Content -LiteralPath (Join-Path $Root 'scripts\Common.ps1') -Raw -Encoding UTF8
        if ($commonSource -notmatch 'if \(\$build -lt 10240\)' -or $commonSource -notmatch "nativeArchitecture -ne 'AMD64'") { throw 'Owner build gate mismatch' }
    }
    Invoke-Case 'Compatibility: target diagnostics script is included' {
        $diagPath = Join-Path $Root 'scripts\Check-WindowsTarget.ps1'
        $bytes = [IO.File]::ReadAllBytes($diagPath)
        if ($bytes.Length -lt 3 -or $bytes[0] -ne 239 -or $bytes[1] -ne 187 -or $bytes[2] -ne 191) { throw 'Target diagnostics script not PowerShell 5.1 safe' }
    }

    Invoke-Case 'Diagnostics: redacts bearer secrets and local paths' {
        $value = Protect-DiagnosticText ('Bearer fixture-token api_key=fixture-key ' + $Root + ' ' + $env:USERPROFILE)
        if ($value -match 'fixture-token|fixture-key' -or $value.Contains($env:USERPROFILE)) { throw 'Фильтр диагностики пропустил секрет/путь.' }
    }

    Invoke-Case 'Rollback: partially updated known files accepted, unknown content rejected' {
        $oldDir=Join-Path $fixture 'recovery-old'; New-OwnedDirectory $oldDir
        $newDir=Join-Path $fixture 'recovery-new'; New-OwnedDirectory $newDir
        $mixedDir=Join-Path $fixture 'recovery-mixed'; New-OwnedDirectory $mixedDir
        [IO.File]::WriteAllText((Join-Path $oldDir 'app.dll'),'old')
        [IO.File]::WriteAllText((Join-Path $newDir 'app.dll'),'new')
        $oldInventory=[pscustomobject]@{files=@(Get-TestFileRecord $oldDir 'app.dll')}
        $newInventory=[pscustomobject]@{files=@(Get-TestFileRecord $newDir 'app.dll')}
        $oldPath=Join-Path $oldDir 'installed-files.json'; $oldInventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $oldPath -Encoding UTF8
        $newPath=Join-Path $newDir 'installed-files.json'; $newInventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $newPath -Encoding UTF8
        Copy-Item -LiteralPath (Join-Path $newDir 'app.dll') -Destination (Join-Path $mixedDir 'app.dll')
        Copy-Item -LiteralPath $oldPath -Destination (Join-Path $mixedDir 'installed-files.json')
        Assert-RecoverableInstall $mixedDir $oldInventory $newInventory $oldPath $newPath
        [IO.File]::WriteAllText((Join-Path $mixedDir 'foreign.txt'),'keep')
        Expect-Rejection { Assert-RecoverableInstall $mixedDir $oldInventory $newInventory $oldPath $newPath } 'Неизвестный'
        if ([IO.File]::ReadAllText((Join-Path $mixedDir 'foreign.txt')) -ne 'keep') { throw 'Unknown file changed' }
    }

    Invoke-Case 'Upgrade: removes only verified retired files and empty owned folders' {
        $directory=Join-Path $fixture 'obsolete'; New-OwnedDirectory (Join-Path $directory 'old\licenses')
        [IO.File]::WriteAllText((Join-Path $directory 'old\licenses\notice.txt'),'known obsolete')
        [IO.File]::WriteAllText((Join-Path $directory 'keep.dll'),'current')
        [IO.File]::WriteAllText((Join-Path $directory 'release-installed.json'),'marker')
        $oldInventory=[pscustomobject]@{version='0.1.28';files=@((Get-TestFileRecord $directory 'old\licenses\notice.txt'),(Get-TestFileRecord $directory 'keep.dll'))}
        $newInventory=[pscustomobject]@{version=$Version;files=@((Get-TestFileRecord $directory 'keep.dll'),(Get-TestFileRecord $directory 'release-installed.json'))}
        Assert-InPlaceUpgradeCompatible $oldInventory $newInventory
        Remove-ObsoleteInstallFiles $directory $oldInventory $newInventory
        if ((Test-Path -LiteralPath (Join-Path $directory 'old')) -or [IO.File]::ReadAllText((Join-Path $directory 'keep.dll')) -ne 'current') { throw 'Retired file cleanup failed' }
        [IO.File]::WriteAllText((Join-Path $directory 'changed.txt'),'old')
        $oldInventory=[pscustomobject]@{files=@(Get-TestFileRecord $directory 'changed.txt')}
        [IO.File]::WriteAllText((Join-Path $directory 'changed.txt'),'changed')
        Expect-Rejection { Remove-ObsoleteInstallFiles $directory $oldInventory $newInventory } 'Устаревший файл изменён'
        if ([IO.File]::ReadAllText((Join-Path $directory 'changed.txt')) -ne 'changed') { throw 'Unknown change lost' }
    }
    $failed = @($results | Where-Object { -not $_.passed }).Count
    New-OwnedDirectory $Artifacts
    [pscustomobject]@{version=$Version; suite='PowerShell bootstrap/update'; executedOnWindows=$true; powershell=$PSVersionTable.PSVersion.ToString(); total=$results.Count; failed=$failed; cases=@($results.ToArray())} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Artifacts 'bootstrap-tests.json') -Encoding UTF8
    foreach ($result in $results) {
        $statusLabel = if ($result.passed) { 'PASS' } else { 'FAIL' }
        $message = ('{0}: {1}' -f $statusLabel, $result.name)
        if (-not $result.passed -and $result.detail) { $message += ' | ' + $result.detail }
        Write-Host $message
    }
    if ($failed -gt 0) { throw ('Проверки подготовки не прошли: ' + $failed + '. Подробности — в диагностическом архиве.') }
    Write-Host ('Проверки подготовки и отката пройдены: ' + $results.Count) -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $fixture) { Assert-PlainPath $fixture; Remove-Item -LiteralPath $fixture -Recurse -Force }
}
