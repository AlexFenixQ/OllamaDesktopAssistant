# Validates that the synthetic 1C-like fixtures are intact. PowerShell 5.1.
. (Join-Path $PSScriptRoot 'Common.ps1')
$ErrorActionPreference = 'Stop'
$dataset = Join-Path $Root 'test_data'
$meta = Get-Content -LiteralPath (Join-Path $dataset 'ODA_1C_Test_Data_Metrics.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($entry in @(@('ODA_1C_Synthetic_StressTest_2026.xlsx',$meta.xlsx_sha256),@('ODA_1C_120000_Synthetic_Sales.csv',$meta.csv_sha256))) {
    $source = Join-Path $dataset $entry[0]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Synthetic fixture missing' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry[1]) { throw 'Synthetic fixture hash mismatch' }
}
$csv = Join-Path $dataset 'ODA_1C_120000_Synthetic_Sales.csv'
$count = 0
$reader = [IO.StreamReader]::new($csv, [Text.Encoding]::UTF8, $true)
try {
    $header = $reader.ReadLine()
    if ($header -notlike 'Документ*') { throw 'CSV header mismatch' }
    while ($null -ne $reader.ReadLine()) { $count++ }
} finally { $reader.Dispose() }
if ($count -ne 120000) { throw ('Expected 120000 rows, found ' + $count) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $dataset 'ODA_1C_Synthetic_StressTest_2026.xlsx'))
try {
    foreach ($needed in @('[Content_Types].xml','xl/workbook.xml')) {
        if ($null -eq $zip.GetEntry($needed)) { throw ('Workbook missing ' + $needed) }
    }
} finally { $zip.Dispose() }
Write-Host ('Synthetic 1C-like fixture verified: ' + $count + ' CSV rows, valid XLSX container.') -ForegroundColor Green
