param([switch]$Rebuild)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
$payload=Get-Content -LiteralPath (Join-Path $Root 'deployment\native-payload.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$receiptPath=if($env:ODA_NATIVE_CACHE){Join-Path ([IO.Path]::GetFullPath($env:ODA_NATIVE_CACHE)) 'build-receipt.json'}else{Join-Path $Tools 'native\build-receipt.json'}
$sourceHash=(Get-FileHash -LiteralPath (Join-Path $Root 'deployment\native-sources.json')).Hash
$configHash=(Get-FileHash -LiteralPath $PSCommandPath).Hash
if(Test-Path -LiteralPath $receiptPath){$receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8|ConvertFrom-Json;if($receipt.sourceHash -eq $sourceHash -and $receipt.configHash -eq $configHash){$payload=$receipt}}
$ready=$true
foreach($f in $payload.files){$p=Join-Path $Root $f.path;if(-not(Test-Path -LiteralPath $p)){$ready=$false;continue};if((Get-FileHash -LiteralPath $p).Hash -ne $f.sha256){throw 'Встроенная медиабиблиотека изменена. Используйте проверенный полный архив.'}}
if($ready -and -not $Rebuild){Write-Host 'Встроенные медиабиблиотеки проверены по SHA-256.';return}
$cache=if($env:ODA_NATIVE_CACHE){[IO.Path]::GetFullPath($env:ODA_NATIVE_CACHE)}else{Join-Path $Tools 'native'};New-OwnedDirectory $cache
$vendor=Join-Path $Root 'deployment\native-sources'
$pins=Get-Content -LiteralPath (Join-Path $Root 'deployment\native-sources.json') -Raw -Encoding UTF8 | ConvertFrom-Json
function Fetch-Pinned($url,$file,$hash){
 if(Test-Path -LiteralPath $file){if((Get-FileHash -LiteralPath $file).Hash -ne $hash){throw 'SHA-256 исходной зависимости не совпадает.'};return}
 Get-OfficialFile $url $file
 if((Get-FileHash -LiteralPath $file).Hash -ne $hash){throw 'SHA-256 загруженной зависимости не совпадает.'}
}
New-OwnedDirectory $vendor
foreach($f in $pins.sources){Fetch-Pinned $f.url (Join-Path $vendor $f.file) $f.sha256}
$compiler=Join-Path $cache 'w64devkit-x64-2.11.0.7z.exe'
Fetch-Pinned $pins.compiler.url $compiler $pins.compiler.sha256
$bin=Join-Path $cache 'w64devkit\bin'
if(-not(Test-Path -LiteralPath (Join-Path $bin 'gcc.exe'))){$p=Start-Process -FilePath $compiler -ArgumentList @('-y',('-o"'+$cache+'"')) -WindowStyle Hidden -Wait -PassThru;if($p.ExitCode -ne 0){throw 'Не удалось распаковать закреплённый компилятор.'}}
$env:PATH=$bin+';'+$env:PATH
$perlPath=Join-Path $env:ProgramFiles 'Git\usr\bin\perl.exe'
if(-not(Test-Path -LiteralPath $perlPath)){
 $perlZip=Join-Path $cache 'strawberry-perl-5.42.3.1-64bit-portable.zip'
 Fetch-Pinned $pins.perl.url $perlZip $pins.perl.sha256
 $perlRoot=Join-Path $cache 'perl-5.42.3.1';New-OwnedDirectory $perlRoot
 if(-not(Test-Path -LiteralPath (Join-Path $perlRoot 'perl\bin\perl.exe'))){Expand-Archive -LiteralPath $perlZip -DestinationPath $perlRoot}
 $perlPath=Join-Path $perlRoot 'perl\bin\perl.exe'
 if(-not(Test-Path -LiteralPath $perlPath)){throw 'Закреплённый Perl не найден после распаковки.'}
}

$ffmpeg=Join-Path $cache 'ffmpeg-9.0.2'
if(-not(Test-Path -LiteralPath $ffmpeg)){Invoke-Checked (Join-Path $env:SystemRoot 'System32\tar.exe') @('-xf',(Join-Path $vendor 'ffmpeg-9.0.2.tar.xz'),'-C',$cache)}
$aomSource=Join-Path $cache 'libaom-3.15.1'
if(-not(Test-Path -LiteralPath $aomSource)){Invoke-Checked (Join-Path $env:SystemRoot 'System32\tar.exe') @('-xf',(Join-Path $vendor 'libaom-3.15.1.tar.gz'),'-C',$cache)}
$aomBuild=Join-Path $cache 'aom-build';$aomInstall=Join-Path $cache 'aom-install'
$aomPreference=$ErrorActionPreference; $ErrorActionPreference='Continue'
& (Join-Path $bin 'cmake.exe') -S $aomSource -B $aomBuild -G Ninja ('-DPERL_EXECUTABLE='+$perlPath) '-DCMAKE_BUILD_TYPE=Release' '-DCMAKE_C_COMPILER=gcc' '-DCMAKE_CXX_COMPILER=g++' ('-DCMAKE_INSTALL_PREFIX='+$aomInstall) '-DBUILD_SHARED_LIBS=OFF' '-DAOM_TARGET_CPU=generic' '-DCONFIG_AV1_DECODER=1' '-DCONFIG_AV1_ENCODER=0' '-DCONFIG_AV1_HIGHBITDEPTH=1' '-DENABLE_DOCS=OFF' '-DENABLE_EXAMPLES=OFF' '-DENABLE_TOOLS=OFF' '-DENABLE_TESTS=OFF' '-DENABLE_TESTDATA=OFF' '-DCONFIG_WEBM_IO=0' *> (Join-Path $cache 'aom-configure.log')
if($LASTEXITCODE -ne 0){throw 'AV1 configure failed; see aom-configure.log'}
& (Join-Path $bin 'cmake.exe') --build $aomBuild --parallel 8 *> (Join-Path $cache 'aom-build.log')
if($LASTEXITCODE -ne 0){throw 'AV1 compile failed; see aom-build.log'}
& (Join-Path $bin 'cmake.exe') --install $aomBuild *> (Join-Path $cache 'aom-install.log')
if($LASTEXITCODE -ne 0){throw 'AV1 install failed'}
$ErrorActionPreference=$aomPreference
$env:PKG_CONFIG_PATH=(Join-Path $aomInstall 'lib\pkgconfig');$env:PKG_CONFIG_LIBDIR=$env:PKG_CONFIG_PATH
$flags=@('--target-os=mingw32','--arch=x86_64','--cc=gcc','--disable-gpl','--disable-version3','--disable-nonfree','--disable-autodetect','--disable-network','--disable-everything','--disable-doc','--disable-debug','--disable-ffplay','--disable-avdevice','--disable-x86asm','--disable-shared','--enable-static','--enable-ffmpeg','--enable-ffprobe','--enable-avcodec','--enable-avformat','--enable-avfilter','--enable-swresample','--enable-swscale','--enable-protocol=file,pipe','--enable-demuxers','--enable-parsers','--enable-decoders','--disable-decoder=av1','--disable-hwaccels','--pkg-config-flags=--static','--enable-libaom','--enable-decoder=libaom_av1','--enable-encoder=pcm_s16le,mjpeg,mpeg4,aac,flac,wrapped_avframe,pcm_mulaw,msvideo1,ffv1','--enable-muxer=wav,image2,null,mp4,flac,avi','--enable-filter=aresample,aformat,anull,atrim,asetpts,format,scale,select,showinfo,fps,null,trim,setpts','--extra-ldflags=-static')
$ffPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
Push-Location $ffmpeg
try {
 if($true){ & (Join-Path $bin 'sh.exe') './configure' @flags *> (Join-Path $cache 'ffmpeg-configure.log');if($LASTEXITCODE -ne 0){throw 'FFmpeg configure failed; see log'}}
 & (Join-Path $bin 'make.exe') '-j8' 'ffmpeg.exe' 'ffprobe.exe' *> (Join-Path $cache 'ffmpeg-build.log');if($LASTEXITCODE -ne 0){throw 'FFmpeg compile failed; see log'}
}finally{Pop-Location;$ErrorActionPreference=$ffPreference}
$wrapper=Join-Path $cache 'whisper.net-1.9.1'
if(-not(Test-Path -LiteralPath $wrapper)){Expand-Archive -LiteralPath (Join-Path $vendor 'whisper-net-1.9.1.zip') -DestinationPath $cache}
$cpp=Join-Path $wrapper 'whisper.cpp'
if(-not(Test-Path -LiteralPath (Join-Path $cpp 'CMakeLists.txt'))){$temp=Join-Path $cache 'whisper-cpp-unpack';Expand-Archive -LiteralPath (Join-Path $vendor 'whisper-cpp-f24588a.zip') -DestinationPath $temp;$sub=Get-ChildItem -LiteralPath $temp -Directory | Select-Object -First 1;if(Test-Path -LiteralPath $cpp){Get-ChildItem -LiteralPath $cpp -Force | ForEach-Object {throw 'Unexpected whisper subdirectory content'}}else{New-Item -ItemType Directory -Path $cpp | Out-Null};Get-ChildItem -LiteralPath $sub.FullName -Force | Copy-Item -Destination $cpp -Recurse}
$cmakeFile=Join-Path $wrapper 'CMakeLists.txt'
$cmakeText=[IO.File]::ReadAllText($cmakeFile)
if(-not $cmakeText.Contains('Ollama Desktop portable native names')){[IO.File]::AppendAllText($cmakeFile,"`n# Ollama Desktop portable native names`nset_target_properties(whisper PROPERTIES PREFIX `"`" OUTPUT_NAME `"whisper`")`n")}
$cppPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
foreach($flavor in @('cpu','noavx')){
 $build=Join-Path $cache ('whisper-build-'+$flavor)
 $options=@('-S',$wrapper,'-B',$build,'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_C_COMPILER=gcc','-DCMAKE_CXX_COMPILER=g++','-DBUILD_SHARED_LIBS=ON','-DWHISPER_BUILD_TESTS=OFF','-DWHISPER_BUILD_EXAMPLES=OFF','-DGGML_NATIVE=OFF','-DGGML_OPENMP=OFF','-DGGML_BLAS=OFF','-DGGML_CUDA=OFF','-DGGML_VULKAN=OFF','-DGGML_BACKEND_DL=OFF','-DCMAKE_SHARED_LINKER_FLAGS=-static-libgcc -static-libstdc++')
 if($flavor -eq 'noavx'){$options+=@('-DGGML_AVX=OFF','-DGGML_AVX2=OFF','-DGGML_FMA=OFF','-DGGML_F16C=OFF')}
 & (Join-Path $bin 'cmake.exe') @options *> (Join-Path $cache ('whisper-configure-'+$flavor+'.log'));if($LASTEXITCODE -ne 0){throw 'Whisper configure failed; see log'}
 & (Join-Path $bin 'cmake.exe') '--build' $build '--parallel' '8' *> (Join-Path $cache ('whisper-build-'+$flavor+'.log'));if($LASTEXITCODE -ne 0){throw 'Whisper build failed; see log'}
}
$ErrorActionPreference=$cppPreference
'Native compilation passed.'

$target=Join-Path $Root 'src\OllamaDesktop.Core\NativePayload'
New-OwnedDirectory (Join-Path $target 'MediaTools')
foreach($name in @('ffmpeg.exe','ffprobe.exe')){Copy-Item -LiteralPath (Join-Path $ffmpeg $name) -Destination (Join-Path $target ('MediaTools\'+$name)) -Force}
foreach($flavor in @('cpu','noavx')){
 $to=if($flavor -eq 'cpu'){Join-Path $target 'runtimes\win-x64'}else{Join-Path $target 'runtimes\noavx\win-x64'}
 New-OwnedDirectory $to
 foreach($name in @('whisper.dll','ggml-base-whisper.dll','ggml-cpu-whisper.dll','ggml-whisper.dll')){
  $found=@(Get-ChildItem -LiteralPath (Join-Path $cache ('whisper-build-'+$flavor)) -Recurse -Filter $name)
  if($found.Count -ne 1){throw ('Не найдена библиотека '+$name)}
  Copy-Item -LiteralPath $found[0].FullName -Destination (Join-Path $to $name) -Force
 }
}
Write-Host 'FFmpeg и Whisper собраны из закреплённых исходников. Для собственного выпуска обновите инвентарь и подпишите новый релиз.'
$built=@($payload.files|ForEach-Object{$file=Join-Path $Root $_.path;[pscustomobject]@{path=$_.path;sha256=(Get-FileHash -LiteralPath $file).Hash;bytes=(Get-Item -LiteralPath $file).Length}})
New-OwnedDirectory ([IO.Path]::GetDirectoryName($receiptPath))
[pscustomobject]@{sourceHash=$sourceHash;configHash=$configHash;files=$built}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $receiptPath -Encoding UTF8
