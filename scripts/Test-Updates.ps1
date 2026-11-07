$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -eq 5){$env:PSModulePath=$PSHOME+'\Modules;'+$env:SystemRoot+'\System32\WindowsPowerShell\v1.0\Modules'}
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Set-BuildEnvironment
$source=$Root
$test=Join-Path ([IO.Path]::GetTempPath()) ('ODA-Updater-Test-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$testProfileRoot=Join-Path $test 'profile'
$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp=$false
$stub=Join-Path $Artifacts 'update-test-stub'
Invoke-Checked $Dotnet @('publish',(Join-Path $Root 'tests\OllamaDesktop.Update.Tests\UpdaterStub.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-o',$stub) 'update-tests-build.log'
$results=@()
function WriteJson($path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))}
function Hash($path){(Get-FileHash -LiteralPath $path).Hash}
foreach($case in @('installer-failure-restores-app','successful-update-preserves-data','tampered-signature-rejected','unknown-base-file-rejected')){
    $caseProfile=Join-Path $testProfileRoot $case
    $install=Join-Path $caseProfile 'Programs\OllamaDesktopAssistant';$updateHome=Join-Path $caseProfile 'OllamaDesktopAssistant'
    $stage=Join-Path $updateHome ('Updates\'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $install,$stage,(Join-Path $updateHome 'Data') -Force | Out-Null
    foreach($n in 0..9){[IO.File]::WriteAllText((Join-Path $install ('file-'+$n+'.txt')),'old '+$n)}
    # Lightweight self-contained stub binaries are kept together in the fixture.
    Get-ChildItem -LiteralPath $stub -File | Copy-Item -Destination $install
    Move-Item -LiteralPath (Join-Path $install 'Setup.exe') -Destination (Join-Path $install 'OllamaDesktop.exe')
    [IO.File]::WriteAllText((Join-Path $updateHome 'Data\history.db'),'synthetic history')
    [IO.File]::WriteAllText((Join-Path $updateHome 'Data\key.dpapi'),'synthetic encrypted key fixture')
    WriteJson (Join-Path $install 'release-installed.json') @{productId='OllamaDesktopAssistant';version='0.1.30';dataSchema=1}
    $old=@(Get-ChildItem -LiteralPath $install -File | ForEach-Object {[pscustomobject]@{path=$_.Name;sha256=(Hash $_.FullName);bytes=$_.Length}})
    WriteJson (Join-Path $install 'installed-files.json') @{version='0.1.30';files=$old}
    $oldHash=Hash (Join-Path $install 'file-0.txt');$dataHash=Hash (Join-Path $updateHome 'Data\history.db');$keyHash=Hash (Join-Path $updateHome 'Data\key.dpapi')
    Get-ChildItem -LiteralPath $stub -File | Copy-Item -Destination $stage
    $payload=Join-Path $stage 'payload';New-Item -ItemType Directory -Path $payload | Out-Null
    Get-ChildItem -LiteralPath $install -File | Where-Object Name -ne 'installed-files.json' | Copy-Item -Destination $payload
    [IO.File]::WriteAllText((Join-Path $payload 'file-0.txt'),'updated')
    WriteJson (Join-Path $payload 'release-installed.json') @{productId='OllamaDesktopAssistant';version='0.1.31';dataSchema=1}
    $new=@(Get-ChildItem -LiteralPath $payload -File | ForEach-Object {[pscustomobject]@{path=$_.Name;sha256=(Hash $_.FullName);bytes=$_.Length}})
    WriteJson (Join-Path $stage 'installed-files.json') @{version='0.1.31';files=$new}
    $m=@{productId='OllamaDesktopAssistant';version='0.1.31';channel='stable';minimumVersion='0.1.30';installerSha256=(Hash (Join-Path $stage 'Setup.exe'));installerBytes=(Get-Item (Join-Path $stage 'Setup.exe')).Length;inventorySha256=(Hash (Join-Path $stage 'installed-files.json'))}
    WriteJson (Join-Path $stage 'update.json') $m
    $sig=$rsa.SignData([IO.File]::ReadAllBytes((Join-Path $stage 'update.json')),'SHA256');if($case -eq 'tampered-signature-rejected'){$sig[0]=$sig[0] -bxor 1}
    [IO.File]::WriteAllBytes((Join-Path $stage 'update.json.sig'),$sig)
    WriteJson (Join-Path $stage 'job.json') @{productId='OllamaDesktopAssistant';installDirectory=$install;fromVersion='0.1.30';channel='stable';pid=2147483640;processStart=0}
    if($case -eq 'installer-failure-restores-app'){[IO.File]::WriteAllText((Join-Path $stage 'fail.flag'),'fail')}
    if($case -eq 'unknown-base-file-rejected'){[IO.File]::WriteAllText((Join-Path $install 'unknown.txt'),'must not overwrite')}
    $worker=[IO.File]::ReadAllText((Join-Path $source 'scripts\Apply-Update.ps1'))
    $boundKey=[IO.File]::ReadAllText((Join-Path $source 'deployment\update-public-key.xml'))
    $worker=$worker.Replace($boundKey,$rsa.ToXmlString($false))
    [IO.File]::WriteAllText((Join-Path $stage 'Apply-Update.ps1'),$worker,[Text.UTF8Encoding]::new($true))
    $psi=[Diagnostics.ProcessStartInfo]::new();$psi.FileName="$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe";$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
    $psi.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $stage 'Apply-Update.ps1')+'" -Stage "'+$stage+'"';$psi.EnvironmentVariables['LOCALAPPDATA']=$caseProfile
    $p=[Diagnostics.Process]::Start($psi);if(-not $p.WaitForExit(60000)){throw 'Worker timed out'}
    $last=Get-Content -LiteralPath (Join-Path $updateHome 'last-update.json') -Raw | ConvertFrom-Json
    if($case -eq 'successful-update-preserves-data'){$passed=$last.success -eq $true -and (Hash (Join-Path $install 'file-0.txt')) -ne $oldHash}
    elseif($case -eq 'installer-failure-restores-app'){$passed=$last.success -eq $false -and $last.restored -eq $true -and (Hash (Join-Path $install 'file-0.txt')) -eq $oldHash}
    else{$expected=if($case -eq 'tampered-signature-rejected'){'UPDATE_SIGNATURE'}else{'UNKNOWN_FILE: unknown.txt'};$passed=$last.success -eq $false -and $last.code -eq $expected -and (Hash (Join-Path $install 'file-0.txt')) -eq $oldHash}
    $passed=$passed -and (Hash (Join-Path $updateHome 'Data\history.db')) -eq $dataHash -and (Hash (Join-Path $updateHome 'Data\key.dpapi')) -eq $keyHash
    $results+=[pscustomobject]@{name=$case;passed=$passed;workerExit=$p.ExitCode;code=$(if($last.PSObject.Properties.Name -contains 'code'){$last.code}else{''})}
}
$rsa.Dispose()
$results | ConvertTo-Json -Depth 4 | Tee-Object -FilePath (Join-Path $Artifacts 'update-worker-tests.json')
if(@($results|Where-Object passed -ne $true).Count){throw 'Worker test failed'}
