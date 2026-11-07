. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Quiet
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$output=Join-Path $Artifacts 'source'; New-OwnedDirectory $output
$archive=Join-Path $output ('OllamaDesktopAssistant-'+$Version+'-FullSource.zip')
if(Test-Path -LiteralPath $archive){throw 'Архив уже существует. Перезапись без проверки не выполняется.'}
$manifest=Get-Content -LiteralPath (Join-Path $Root 'MANIFEST.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($name in @($manifest.files | ForEach-Object path)+@('MANIFEST.json')){
        $relative=$name.Replace('\','/')
        if($relative -match '(?i)(^|/)(Data|Backups|ReleaseSigning|qa-runtime)(/|$)' -or $relative -match '(?i)\.(dpapi|pfx|p12|key)$'){throw 'В пакет включён недопустимый частный файл.'}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $Root $name),('OllamaDesktopAssistant-'+$Version+'/'+$relative),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {$zip.Dispose()}
$sha=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
($sha+'  '+[IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath ($archive+'.sha256') -Encoding ASCII
Write-Host ('Полный архив: '+$archive)
Write-Host ('SHA-256: '+$sha)
