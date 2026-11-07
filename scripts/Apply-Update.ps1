param([Parameter(Mandatory=$true)][string]$Stage,[switch]$Rollback)
# Windows PowerShell 5.1. Executed by the app after a verified download.
$env:PSModulePath = $PSHOME + '\Modules;' + $env:SystemRoot + '\System32\WindowsPowerShell\v1.0\Modules'
Import-Module Microsoft.PowerShell.Utility
Import-Module Microsoft.PowerShell.Management
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Plain([string]$Path) {
    $p=[IO.Path]::GetFullPath($Path)
    while($p) { if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){ throw 'REPARSE_PATH' }; $p=[IO.Path]::GetDirectoryName($p) }
}
function Child([string]$Root,[string]$Relative) {
    if([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or ($Relative -split '[\\/]' | Where-Object {$_ -eq '..' -or $_ -eq '.'})){throw 'UNSAFE_INVENTORY'}
    $p=[IO.Path]::GetFullPath((Join-Path $Root $Relative)); if(-not $p.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'UNSAFE_INVENTORY'}; Plain $p; return $p
}
function Inventory([string]$Root,$Manifest,[bool]$RejectUnknown=$true) {
    Plain $Root
    if(@($Manifest.files).Count -lt 10 -or @($Manifest.files).Count -gt 3000){throw 'INVENTORY_COUNT'}
    $seen=@{}
    foreach($f in $Manifest.files) { $p=Child $Root $f.path; if($seen.ContainsKey($p)){throw 'DUPLICATE_FILE'}; $seen[$p]=$true; if(-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Hash $p) -ne $f.sha256){throw ('INVENTORY_HASH: '+$f.path)} }
    if($RejectUnknown) { foreach($f in Get-ChildItem -LiteralPath $Root -File -Recurse -Force){
        Plain $f.FullName
        $rel=$f.FullName.Substring($Root.Length).TrimStart('\')
        if(-not $seen.ContainsKey($f.FullName) -and $rel -notin @('installed-files.json','.build-owned') -and $rel -notmatch '^unins\d+\.(exe|dat|msg)$'){throw ('UNKNOWN_FILE: '+$rel)}
    } }
}
function ZipHash($Entry) {
    $stream=$Entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
}
function CopyVerified([string]$Source,[string]$Destination) {
    Plain $Source; Plain $Destination
    $list=@();$zip=[IO.Compression.ZipFile]::Open($Destination,[IO.Compression.ZipArchiveMode]::Create)
    try { foreach($f in Get-ChildItem -LiteralPath $Source -File -Recurse -Force){
        Plain $f.FullName;$relative=$f.FullName.Substring($Source.Length).TrimStart('\');$sha=Hash $f.FullName
        $entry=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$f.FullName,$relative.Replace('\','/'),[IO.Compression.CompressionLevel]::Fastest)
        $list+=[pscustomobject]@{path=$relative;sha256=$sha}
        if((Hash $f.FullName) -ne $sha){throw 'SOURCE_CHANGED_DURING_BACKUP'}
    } } finally { $zip.Dispose() }
    $zip=[IO.Compression.ZipFile]::OpenRead($Destination)
    try { foreach($f in $list){$entry=$zip.GetEntry($f.path.Replace('\','/'));if(-not $entry -or (ZipHash $entry) -ne $f.sha256){throw 'BACKUP_HASH'}} } finally { $zip.Dispose() }
    return ,$list
}
function Restore-App {
    $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $backup 'app.zip'))
    try {
        foreach($f in $oldBackup){$entry=$zip.GetEntry($f.path.Replace('\','/'));if(-not $entry -or (ZipHash $entry) -ne $f.sha256){throw 'BACKUP_TAMPERED'}}
        foreach($f in $newInventory.files){$p=Child $install $f.path;if((Test-Path -LiteralPath $p) -and -not ($oldBackup | Where-Object path -eq $f.path)){if((Hash $p) -ne $f.sha256){throw 'ROLLBACK_UNKNOWN_FILE'};Remove-Item -LiteralPath $p -Force}}
        foreach($f in $oldBackup){$p=Child $install $f.path;[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)) | Out-Null;[IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($f.path.Replace('\','/')),$p,$true)}
    } finally { $zip.Dispose() }
    if($registrySaved){ & reg.exe import (Join-Path $backup 'uninstall.reg') | Out-Null;if($LASTEXITCODE -ne 0){throw 'ROLLBACK_REGISTRY'} }
    Inventory $install $oldInventory
}
$permit=$null; $mutated=$false; $registrySaved=$false; $backup=$null; $install=$null; $oldBackup=@()
try {
    $Stage=[IO.Path]::GetFullPath($Stage).TrimEnd('\'); Plain $Stage
    $updateHome=Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant'; $updates=Join-Path $updateHome 'Updates'
    if([IO.Path]::GetDirectoryName($Stage) -ne $updates -or [IO.Path]::GetFileName($Stage) -notmatch '^[0-9a-f]{32}$'){throw 'UNKNOWN_STAGE'}
    $job=Get-Content -LiteralPath (Join-Path $Stage 'job.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $install=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\OllamaDesktopAssistant')).TrimEnd('\')
    if($job.productId -ne 'OllamaDesktopAssistant' -or $job.installDirectory -ne $install){throw 'UNKNOWN_INSTALL'}; Plain $install
    if($Rollback){throw 'Use scripts/Rollback-SilentUpdate.ps1 for an already completed update.'}
    $key='<RSAKeyValue><Modulus>n1IRJ1PucW2jtnydbNuIafVEkTa3JAxll71Sf/+uB6ej8knzSg97ZlAtiYGTgldxibDO9qGVN6ui//r1fQnZSLMoYNB9/40hqssOelKY71FNJJzmdxw5M3lzy/rGBA5VpD/8+BViHz1MDiBSICpd9cZBSn2ipX62wUfeBiOa/UZfXYRORDwyfgvbo+ueYQKMwx6EU3+P9YYIR6vHXiVsZnBihbUL1D8hkwb46krPmXTFSwuRkl8TOZNHhd6h0KpS3C2Bc3xkBUWZBzjwO+dHDSS4VRjY8uwaOdvuFdg7XYgaB0MBdbnDqVx4/8GriSrY3l03c96DoqG2tcwf1iNhyTgIH0EPEeZjvEEY7tDpVzMdSg9NoZ6IWH3XUE43t5VVwac69oo7iKUxsSQMHmLtvnV7fWYa3fmNPlFyXjkzrqVlXLrDRHVb+AHqs8V2U82Cl6rUViCFuSWx6Gd5Kk95wPKVTu3zk+s/BEhV0lJwWYCiiwMltlEkfmYuX3XwL6n1</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>'
    $rsa=New-Object Security.Cryptography.RSACryptoServiceProvider; $rsa.FromXmlString($key)
    $json=[IO.File]::ReadAllBytes((Join-Path $Stage 'update.json')); $sig=[IO.File]::ReadAllBytes((Join-Path $Stage 'update.json.sig'))
    if($json.Length -gt 64000 -or -not $rsa.VerifyData($json,'SHA256',$sig)){throw 'UPDATE_SIGNATURE'}; $rsa.Dispose()
    $release=[Text.Encoding]::UTF8.GetString($json) | ConvertFrom-Json
    if($release.productId -ne 'OllamaDesktopAssistant' -or $release.version -notmatch '^\d+\.\d+\.\d+(-beta\.\d+)?$' -or $job.channel -notin @('stable','beta') -or ($job.channel -eq 'stable' -and $release.channel -ne 'stable')){throw 'UPDATE_POLICY'}
    if([version](($release.version -split '-')[0]) -lt [version](($job.fromVersion -split '-')[0]) -or [version](($job.fromVersion -split '-')[0]) -lt [version](($release.minimumVersion -split '-')[0])){throw 'VERSION_POLICY'}
    $setup=Join-Path $Stage 'Setup.exe'; if((Hash $setup) -ne $release.installerSha256 -or (Get-Item -LiteralPath $setup).Length -ne $release.installerBytes){throw 'INSTALLER_HASH'}
    $newManifest=Join-Path $Stage 'installed-files.json'; if((Hash $newManifest) -ne $release.inventorySha256){throw 'NEW_INVENTORY_HASH'}
    $newInventory=Get-Content -LiteralPath $newManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if($newInventory.version -ne $release.version){throw 'NEW_INVENTORY_VERSION'}
    $parent=Get-Process -Id $job.pid -ErrorAction SilentlyContinue
    if($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $job.processStart){if(-not $parent.WaitForExit(120000)){throw 'APP_STILL_RUNNING'}}
    # Never terminate another instance, an importer, or an active generation.
    if(Get-Process -Name 'OllamaDesktop' -ErrorAction SilentlyContinue){throw 'APP_RUNNING'}
    $lock=New-Object Threading.Mutex($false,'Local\OllamaDesktopAssistant.Update')
    if(-not $lock.WaitOne(0)){throw 'UPDATE_RUNNING'}
    $marker=Join-Path $install 'release-installed.json'; $oldMarker=Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
    if($oldMarker.productId -ne 'OllamaDesktopAssistant' -or $oldMarker.version -ne $job.fromVersion){throw 'BASE_VERSION'}
    $oldInventory=Get-Content -LiteralPath (Join-Path $install 'installed-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($oldInventory.version -ne $job.fromVersion){throw 'BASE_VERSION'}; Inventory $install $oldInventory
    $backup=Join-Path $updateHome ('Backups\Update-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)); Plain $backup
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    $oldBackup=CopyVerified $install (Join-Path $backup 'app.zip')
    $data=Join-Path $updateHome 'Data'; if(Test-Path -LiteralPath $data){$dataBackup=CopyVerified $data (Join-Path $backup 'data.zip'); $dataBackup | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup 'data-files.json') -Encoding UTF8}
    $oldBackup | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup 'app-files.json') -Encoding UTF8
    $reg='HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4959D6E5-8A31-45A2-B215-992E064C87B9}_is1'
    if(Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4959D6E5-8A31-45A2-B215-992E064C87B9}_is1') { & reg.exe export $reg (Join-Path $backup 'uninstall.reg') /y | Out-Null; if($LASTEXITCODE -ne 0){throw 'BACKUP_REGISTRY'}; $registrySaved=$true }
    $receipt=[pscustomobject]@{productId='OllamaDesktopAssistant';fromVersion=$job.fromVersion;toVersion=$release.version;installDirectory=$install;backup=$backup;state='backup-complete';registrySaved=$registrySaved}
    $receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'receipt.json') -Encoding UTF8
    Copy-Item -LiteralPath $newManifest -Destination (Join-Path $backup 'new-installed-files.json')
    $token=[Guid]::NewGuid().ToString('N'); $permit=Join-Path $updateHome ('UpgradePermits\'+$token+'.txt'); Plain $permit; [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($permit)) | Out-Null
    [IO.File]::WriteAllLines($permit,@('OllamaDesktopAssistant',$release.version,$token,(Hash $marker),(Hash (Join-Path $install 'installed-files.json'))),[Text.Encoding]::ASCII)
    $mutated=$true
    $arguments=@('/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/SP-','/NORESTART','/NOCLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS',('/ODAVERIFY='+$token),('/DIR="'+$install+'"'),('/LOG="'+(Join-Path $Stage 'installer.log')+'"'))
    $process=Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if($process.ExitCode -ne 0){throw ('INSTALLER_EXIT_'+$process.ExitCode)}
    foreach($f in $oldInventory.files){if(-not ($newInventory.files | Where-Object path -eq $f.path)){$p=Child $install $f.path;if(Test-Path -LiteralPath $p){if((Hash $p) -ne $f.sha256){throw 'RETIRED_CHANGED'};Remove-Item -LiteralPath $p -Force}}}
    if((Hash (Join-Path $install 'installed-files.json')) -ne $release.inventorySha256){throw 'INSTALLED_INVENTORY_HASH'}
    Inventory $install $newInventory
    $receipt.state='installed-and-verified'; $receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'receipt.json') -Encoding UTF8
    [pscustomobject]@{success=$true;version=$release.version;backup=$backup} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $updateHome 'last-update.json') -Encoding UTF8
    Start-Process -FilePath (Join-Path $install 'OllamaDesktop.exe') | Out-Null
} catch {
    $code=$_.Exception.Message; $restored=$false
    if($mutated -and $oldBackup.Count -gt 0){try{Restore-App; $restored=$true}catch{$code+='; ROLLBACK_FAILED: '+$_.Exception.Message}}
    if($install){try{[pscustomobject]@{success=$false;code=$code;restored=$restored;backup=$backup} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'OllamaDesktopAssistant\last-update.json') -Encoding UTF8}catch{}}
    if($restored -or (-not $mutated -and $install -and (Test-Path -LiteralPath (Join-Path $install 'OllamaDesktop.exe')) -and -not (Get-Process -Name 'OllamaDesktop' -ErrorAction SilentlyContinue))){Start-Process -FilePath (Join-Path $install 'OllamaDesktop.exe') | Out-Null}
    exit 1
} finally {
    if($permit -and (Test-Path -LiteralPath $permit)){Remove-Item -LiteralPath $permit -Force}
    if(Get-Variable lock -ErrorAction SilentlyContinue){try{$lock.ReleaseMutex()}catch{}; $lock.Dispose()}
}
