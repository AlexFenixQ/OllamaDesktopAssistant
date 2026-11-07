param([switch]$Quiet)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
$manifest = Get-Content -LiteralPath (Join-Path $Root 'MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.productId -ne 'OllamaDesktopAssistant' -or $manifest.version -ne $Version) { throw 'Неверный манифест пакета.' }
foreach ($entry in $manifest.files) {
    if ([IO.Path]::IsPathRooted($entry.path) -or $entry.path.Contains(':') -or ($entry.path.Replace('\','/').Split('/') -contains '..')) { throw 'Небезопасный путь манифеста.' }
    $path = Join-Path $Root $entry.path
    Assert-PlainPath $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Отсутствует файл: ' + $entry.path) }
    if ((Get-Item -LiteralPath $path).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Контрольная сумма не совпадает: ' + $entry.path) }
}
# Fail before build when any historical predecessor baseline is absent or mismatched.
$release = Get-Content -LiteralPath (Join-Path $Root 'RELEASE.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$matchRelease = [regex]::Match([string]$release.version, '^0\.1\.(\d+)$')
if (-not $matchRelease.Success) { throw 'Unsupported release version format.' }
for ($priorIndex=0; $priorIndex -lt [int]$matchRelease.Groups[1].Value; $priorIndex++) {
    $baselinePath = Join-Path $Root ('UPDATE_BASE_01' + $priorIndex + '.json')
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) { throw ('Missing baseline: ' + $baselinePath) }
    $prior = Get-Content -LiteralPath $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($prior.productId -ne $release.productId -or $prior.fromVersion -ne ('0.1.' + $priorIndex) -or $prior.toVersion -ne $release.version) { throw ('Incorrect baseline: ' + $baselinePath) }
    if (@($prior.files | Where-Object { $_.path -eq 'MANIFEST.json' }).Count -ne 1) { throw ('Missing MANIFEST record: ' + $baselinePath) }
}
if (-not $Quiet) { Write-Host ('Проверено файлов: ' + @($manifest.files).Count) -ForegroundColor Green }

# Regression gate: both owner bootstrap and installed-upgrade script MUST accept
# every predecessor recorded in RELEASE.json, before the build can begin.
$releaseNumber = [regex]::Match([string]$release.version, '^0\.1\.(\d+)$')
if (-not $releaseNumber.Success) { throw 'Unsupported release version format.' }
$startScriptContent = Get-Content -LiteralPath (Join-Path $Root 'Start.ps1') -Raw -Encoding UTF8
$upgradeScriptContent = Get-Content -LiteralPath (Join-Path $Root 'scripts\Update-Installed.ps1') -Raw -Encoding UTF8
for ($predecessorIndex = 0; $predecessorIndex -lt [int]$releaseNumber.Groups[1].Value; $predecessorIndex++) {
    $versionLiteral = "'0.1." + $predecessorIndex + "'"
    if (-not $startScriptContent.Contains($versionLiteral) -or -not $upgradeScriptContent.Contains($versionLiteral)) {
        throw ('Installer predecessor version not recognized by both scripts: 0.1.' + $predecessorIndex)
    }
}

# Parse all packaged PowerShell scripts using the actual Windows PowerShell parser before downloads/builds.
foreach ($scriptEntry in @($manifest.files | Where-Object { $_.path.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase) })) {
    $scriptFile = Get-Item -LiteralPath (Join-Path $Root $scriptEntry.path)
    $raw = [IO.File]::ReadAllBytes($scriptFile.FullName)
    if ($raw.Length -lt 3 -or $raw[0] -ne 239 -or $raw[1] -ne 187 -or $raw[2] -ne 191) { throw ('PowerShell 5.1 требует UTF-8 BOM: ' + $scriptFile.Name) }
    $tokens = $null; $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if (@($parseErrors).Count -gt 0) { throw ('Ошибка синтаксиса скрипта: ' + $scriptFile.Name + '. Требуется исправленный архив, а не ручная правка.') }
}

# Fail before downloads or source replacement if independent version/test contracts diverge.
$null = Assert-ReleaseContracts
