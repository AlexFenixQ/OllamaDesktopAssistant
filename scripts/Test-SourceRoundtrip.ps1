param([Parameter(Mandatory=$true)][string]$BaseArchive)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'SourceUpdate.Common.ps1')
Assert-Environment
$fixture = Join-Path $env:LOCALAPPDATA ('ODA.s-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$expectedPrefix = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
if (-not [IO.Path]::GetFullPath($fixture).StartsWith($expectedPrefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($fixture) -notmatch '^ODA\.s-[a-f0-9]{8}$' -or (Test-Path -LiteralPath $fixture)) { throw 'Unsafe test path' }
$fixtureContainer=$fixture
try {
    Expand-SafeZip $BaseArchive $fixture
    if(-not(Test-Path -LiteralPath (Join-Path $fixture 'RELEASE.json'))){
        $packages=@(Get-ChildItem -LiteralPath $fixture -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'RELEASE.json')})
        if($packages.Count -ne 1){throw 'Не найден единственный корень исходного выпуска.'}
        $fixture=$packages[0].FullName
    }
    $before = Get-Content -LiteralPath (Join-Path $fixture 'RELEASE.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $baselineName = Get-SourceBaselineFile ([string]$before.version) $fixture
    $baseline = Get-Content -LiteralPath (Join-Path $Root $baselineName) -Raw -Encoding UTF8 | ConvertFrom-Json
    $privateMarker = Join-Path $fixture 'unrelated-user-data.txt'
    [IO.File]::WriteAllText($privateMarker, 'synthetic data must survive')
    & (Join-Path $Root 'scripts\Apply-SourceUpdate.ps1') -TargetRoot $fixture
    Assert-SourceInventory $fixture @(Get-CurrentInventory $Root)
    & (Join-Path $Root 'scripts\Rollback-SourceUpdate.ps1') -TargetRoot $fixture -ConfirmRollback
    Assert-SourceInventory $fixture @($baseline.files)
    if ([IO.File]::ReadAllText($privateMarker) -ne 'synthetic data must survive') { throw 'Data preservation failed' }
    $readme = Join-Path $fixture 'README_RU.md'
    [IO.File]::AppendAllText($readme, 'synthetic local change')
    $rejected = $false
    try { & (Join-Path $Root 'scripts\Apply-SourceUpdate.ps1') -TargetRoot $fixture }
    catch { $rejected = $true }
    if (-not $rejected -or -not [IO.File]::ReadAllText($readme).EndsWith('synthetic local change')) { throw 'Modified source was overwritten' }
    New-OwnedDirectory $Artifacts
    [pscustomobject]@{ version=$Version; baseVersion=$before.version; baseline=$baselineName; passed=$true; checks=@('full source update','verified rollback','unrelated data preserved','modified source rejected') } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Artifacts ($baselineName.Replace('.json','-roundtrip.json'))) -Encoding UTF8
    Write-Host ('PASS: full update/rollback, ' + $baselineName) -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $fixtureContainer) { Assert-PlainPath $fixtureContainer; Remove-Item -LiteralPath $fixtureContainer -Recurse -Force }
}
