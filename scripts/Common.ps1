Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:Version = '0.1.31'
# Keep SDK / NuGet paths independent of the source directory depth.
$script:LegacyTools = Join-Path $script:Root '.tools'
$script:Tools = if ($env:ODA_BUILD_CACHE) { [IO.Path]::GetFullPath($env:ODA_BUILD_CACHE) } else { Join-Path $env:LOCALAPPDATA 'ODA.b' }
$script:Artifacts = Join-Path $script:Root 'artifacts'
$script:Publish = Join-Path $script:Artifacts 'publish'
$script:InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\OllamaDesktopAssistant'
function Assert-Environment {
    if ($PSVersionTable.PSVersion.Major -lt 5) { throw 'Требуется Windows PowerShell 5.1 или новее.' }
    if (-not [Environment]::Is64BitProcess -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'Запустите 64-разрядную Windows PowerShell на Windows x64.' }
    $osInfo = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $nativeArchitecture = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
    if ($osInfo.InstallationType -ne 'Client' -or $nativeArchitecture -ne 'AMD64') { throw 'Сборка проекта выполняется на клиентской Windows 10/11 x64 (AMD64). На ARM64 целевое приложение устанавливается отдельно.' }
    $build = [int]$osInfo.CurrentBuildNumber
    if ($build -lt 10240) { throw 'Для сборки требуется Windows 10/11 x64 (сборка 10240 или новее).' }
    $release = Get-Content -LiteralPath (Join-Path $script:Root 'RELEASE.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($release.productId -ne 'OllamaDesktopAssistant' -or $release.version -ne $script:Version) { throw 'Неизвестный исходный релиз. Операция остановлена.' }
    foreach ($folder in @($script:Root, $script:Tools, $script:Artifacts)) { if (Test-Path -LiteralPath $folder) { Assert-PlainPath $folder } }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
function Assert-PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Символические ссылки и точки перенаправления в рабочем пути запрещены.' }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }; $current = $parent
    }
}
function New-OwnedDirectory([string]$Path) {
    Assert-PlainPath $Path
    [IO.Directory]::CreateDirectory($Path) | Out-Null
}
function Get-OfficialFile([string]$Url, [string]$Destination) {
    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('builds.dotnet.microsoft.com','dotnetcli.azureedge.net','ci.dot.net','download.visualstudio.microsoft.com','github.com','api.github.com','raw.githubusercontent.com','codeload.github.com','ffmpeg.org')) { throw 'Источник загрузки не входит в список разрешённых.' }
    New-OwnedDirectory ([IO.Path]::GetDirectoryName($Destination))
    $temp = $Destination + '.part-' + [Guid]::NewGuid().ToString('N')
    try {
        Invoke-WebRequest -Uri $uri.AbsoluteUri -OutFile $temp -UseBasicParsing -TimeoutSec 1200 -UserAgent 'OllamaDesktop-OwnerBuild/0.1.31' | Out-Null
        Move-Item -LiteralPath $temp -Destination $Destination -Force
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function Set-BuildEnvironment {
    $sdkRoot = Get-VerifiedSystemSdk
    if (-not $sdkRoot) { $sdkRoot = Join-Path $script:Tools 'dotnet-10.0.403' }
    $script:Dotnet = Join-Path $sdkRoot 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $script:Dotnet)) { throw 'SDK ещё не подготовлен. Запустите Start.ps1.' }
    $env:DOTNET_ROOT = $sdkRoot
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_HOME = Join-Path $script:Tools 'home'
    $env:NUGET_PACKAGES = if ($env:ODA_NUGET_CACHE) { [IO.Path]::GetFullPath($env:ODA_NUGET_CACHE) } else { Join-Path $script:Tools 'nuget' }
    $env:NUGET_XMLDOC_MODE = 'skip'
    $env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
    New-OwnedDirectory $script:Artifacts
}
function Get-VerifiedSystemSdk {
    $candidate = Join-Path $env:ProgramFiles 'dotnet'
    $executable = Join-Path $candidate 'dotnet.exe'
    if ((Test-Path -LiteralPath (Join-Path $candidate 'sdk\10.0.403')) -and (Test-Path -LiteralPath $executable)) {
        Assert-PlainPath $candidate
        $signature = Get-AuthenticodeSignature -LiteralPath $executable
        if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match 'Microsoft Corporation') { return $candidate }
    }
    return $null
}
function Invoke-Checked([string]$Executable, [string[]]$Arguments, [string]$LogName = 'build.log') {
    New-OwnedDirectory $script:Artifacts
    $log = Join-Path $script:Artifacts $LogName
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments 2>&1 | ForEach-Object { $line = $_.ToString(); Write-Host $line; Add-Content -LiteralPath $log -Value $line -Encoding UTF8 }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw ('Операция завершилась с кодом {0}. Изменять исходники вручную не нужно; сохраните диагностический архив.' -f $code) }
}
function Assert-InstalledRelease {
    Assert-PlainPath $script:InstallDir
    $marker = Join-Path $script:InstallDir 'release-installed.json'
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Каталог не является известной установкой Ollama Desktop. Он не будет изменён.' }
    $release = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($release.productId -ne 'OllamaDesktopAssistant' -or $release.version -ne $script:Version) { throw 'Неизвестная базовая версия. Обновление/откат запрещены.' }
    $manifest = Get-Content -LiteralPath (Join-Path $script:InstallDir 'installed-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $manifest.files) {
        if ($entry.path.Contains('..') -or [IO.Path]::IsPathRooted($entry.path) -or $entry.path.Contains(':')) { throw 'Повреждён установленный манифест.' }
        $path = Join-Path $script:InstallDir $entry.path
        Assert-PlainPath $path
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Файлы установленного приложения изменены. Автоматическая операция отменена.' }
    }
}

function Initialize-ToolsDirectory {
    Assert-PlainPath $script:Tools
    $marker = Join-Path $script:Tools '.oda-build-cache.json'
    if (Test-Path -LiteralPath $script:Tools) {
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw 'Короткая папка ODA.b уже существует без метки проекта. Чужие файлы не изменяются.'
        }
        $owner = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($owner.productId -ne 'OllamaDesktopAssistant' -or $owner.schema -ne 1 -or $owner.ownerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
            throw 'Неизвестный владелец служебной папки ODA.b. Операция отменена.'
        }
    } else {
        New-OwnedDirectory $script:Tools
        [pscustomobject]@{ productId='OllamaDesktopAssistant'; schema=1; ownerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value } |
            ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
    }
}

function Get-SafeZipPlan([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Assert-PlainPath $Archive
    Assert-PlainPath $Destination
    $rootPath = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    $prefix = $rootPath + '\'
    if ($rootPath.Length -ge 248) { throw 'Каталог распаковки слишком длинный для Windows PowerShell 5.1.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $fileNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $directoryNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$total = 0
    $maximum = 0
    try {
        if ($zip.Entries.Count -gt 100000) { throw 'В архиве слишком много элементов.' }
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            if ([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or $name.Contains(':')) { throw 'Небезопасный путь внутри архива.' }
            $directory = $name.EndsWith('\')
            $key = $name.TrimEnd('\')
            foreach ($component in $key.Split('\')) {
                if ([string]::IsNullOrWhiteSpace($component) -or $component -in @('.', '..') -or $component.EndsWith('.') -or $component.EndsWith(' ') -or $component.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $component -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
                    throw 'Небезопасное имя элемента ZIP.'
                }
            }
            if (-not $names.Add($key)) { throw 'Повторяющиеся пути в ZIP (в том числе с разным регистром).' }
            if ($entry.PSObject.Properties.Name -contains 'ExternalAttributes') {
                if ((($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960) { throw 'Символические ссылки в ZIP запрещены.' }
            }
            $path = [IO.Path]::GetFullPath((Join-Path $rootPath $key))
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Путь ZIP выходит за пределы распаковки.' }
            if ($path.Length -ge 260 -or ($directory -and $path.Length -ge 248) -or [IO.Path]::GetDirectoryName($path).Length -ge 248) {
                throw ('Распаковка остановлена ДО записи: длина пути {0}. Используется короткая папка ODA.b, но профиль Windows слишком глубокий для этого архива. Системные настройки не менялись.' -f $path.Length)
            }
            if ($path.Length -gt $maximum) { $maximum = $path.Length }
            if ($directory) { [void]$directoryNames.Add($key) } else { [void]$fileNames.Add($key) }
            $parent = [IO.Path]::GetDirectoryName($key)
            while ($parent) { [void]$directoryNames.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
            $total += $entry.Length
            if ($entry.Length -gt 2147483648 -or $total -gt 8589934592) { throw 'Размер распакованных данных превышает лимит.' }
        }
        foreach ($name in $fileNames) { if ($directoryNames.Contains($name)) { throw 'Конфликт файла и каталога в ZIP.' } }
        return [pscustomobject]@{ entries=$zip.Entries.Count; expandedBytes=$total; maxPathLength=$maximum }
    } finally { $zip.Dispose() }
}

function Expand-SafeZip([string]$Archive, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw 'Каталог распаковки уже существует. Неизвестные файлы не перезаписываются.' }
    $plan = Get-SafeZipPlan $Archive $Destination
    Write-Host ('Проверка ZIP: {0} элементов; самый длинный путь — {1} символов.' -f $plan.entries, $plan.maxPathLength)
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination)
}

function Protect-DiagnosticText([string]$Text) {
    $safe = $Text
    foreach ($pair in @(@($script:Root,'<SOURCE>'), @($script:Tools,'<BUILD_CACHE>'), @($env:USERPROFILE,'<USER>'))) {
        if (-not [string]::IsNullOrEmpty($pair[0])) { $safe = $safe.Replace($pair[0], $pair[1]) }
    }
    $safe = [regex]::Replace($safe, '(?i)(bearer\s+|api[_-]?key\s*[:=]\s*)\S+', '$1[REDACTED]')
    $safe = [regex]::Replace($safe, '(?i)(https?://)[^\s/@]+:[^\s/@]+@', '$1[REDACTED]@')
    return $safe
}

function Save-BuildFailure($Record) {
    New-OwnedDirectory $script:Artifacts
    $message = $Record.Exception.ToString() + [Environment]::NewLine + $Record.ScriptStackTrace
    $safe = Protect-DiagnosticText $message
    [pscustomobject]@{ version=$script:Version; utc=[DateTime]::UtcNow.ToString('o'); error=$safe } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $script:Artifacts 'last-failure.json') -Encoding UTF8
}

# One release contract, shared by preparation, tests, build and installer.
function Get-ReleaseTestCount {
    $releasePath = Join-Path $script:Root 'RELEASE.json'
    $release = Get-Content -LiteralPath $releasePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($release.productId -ne 'OllamaDesktopAssistant' -or $release.version -ne $script:Version) { throw 'Неверная версия RELEASE.json.' }
    $expected = [int]$release.expectedWindowsTests
    if ($expected -lt 1 -or $expected -gt 1000) { throw 'Неверное ожидаемое количество тестов.' }
    return $expected
}
function Assert-ReleaseContracts {
    $expected = Get-ReleaseTestCount
    $status = Get-Content -LiteralPath (Join-Path $script:Root 'TEST_STATUS.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $status.PSObject.Properties['expectedTotalWindowsTests']) { throw 'TEST_STATUS.json: отсутствует expectedTotalWindowsTests. Пакет выпуска поврежден.' }
    if ($status.version -ne $script:Version -or [int]$status.expectedTotalWindowsTests -ne $expected) {
        throw 'Количество или версия тестов не совпадают с RELEASE.json.'
    }
    $models = Get-Content -LiteralPath (Join-Path $script:Root 'src\OllamaDesktop.Core\Models.cs') -Raw -Encoding UTF8
    if ($models -notmatch ('public const string Version\s*=\s*"' + [regex]::Escape($script:Version) + '"')) { throw 'Версия BuildInfo не совпадает с релизом.' }
    $props = Get-Content -LiteralPath (Join-Path $script:Root 'Directory.Build.props') -Raw -Encoding UTF8
    if ($props -notmatch ('<Version>' + [regex]::Escape($script:Version) + '</Version>')) { throw 'Версия сборки .NET не совпадает с релизом.' }
    $installer = Get-Content -LiteralPath (Join-Path $script:Root 'deployment\OllamaDesktop.iss') -Raw -Encoding UTF8
    if ($installer -notmatch ('(?m)^AppVersion=' + [regex]::Escape($script:Version) + '\r?$') -or
        $installer -notmatch ('(?m)^OutputBaseFilename=OllamaDesktop-' + [regex]::Escape($script:Version) + '-win-x64-Setup\r?$')) {
        throw 'Версия установщика не совпадает с релизом.'
    }

    if ($installer -notmatch '(?m)^MinVersion=10\.0\.10240\r?$' -or $installer -notmatch '(?m)^ArchitecturesAllowed=x64compatible\r?$') {
        throw 'Требования совместимости Windows 10/11 в установщике не соответствуют релизу.'
    }
    $wpfStartup = Get-Content -LiteralPath (Join-Path $script:Root 'src\OllamaDesktop.App\App.xaml.cs') -Raw -Encoding UTF8
    if ($wpfStartup -notmatch 'IsWindowsVersionAtLeast\(10, 0, 10240\)') {
        throw 'В WPF остаётся запрет запуска на Windows 10.'
    }

    # Keep the source, declared count and packaging entry points on ONE release.
    $testsFile = Join-Path $script:Root 'tests\OllamaDesktop.Tests\Program.cs'
    $testsText = Get-Content -LiteralPath $testsFile -Raw -Encoding UTF8
    $testCount = [regex]::Matches($testsText, '(?m)^\s*await Test(?:Async)?\(').Count
    if ($testCount -ne $expected) { throw ('Количество реализованных тестов (' + $testCount + ') не совпадает с метаданными (' + $expected + ').') }
    $escaped = [regex]::Escape($script:Version)
    $start = Get-Content -LiteralPath (Join-Path $script:Root 'Start.ps1') -Raw -Encoding UTF8
    $package = Get-Content -LiteralPath (Join-Path $script:Root 'scripts\Package.ps1') -Raw -Encoding UTF8
    $build = Get-Content -LiteralPath (Join-Path $script:Root 'scripts\Build.ps1') -Raw -Encoding UTF8
    foreach ($record in @($start, $package)) {
        if ($record -notmatch ('OllamaDesktop-' + $escaped + '-win-x64-Setup\.exe')) { throw 'Имя установщика не соответствует текущему релизу.' }
    }
    if ($build -notmatch ('OllamaDesktopAssistant ' + $escaped)) { throw 'Маркер публикации не соответствует релизу.' }
    $xamlPath = Join-Path $script:Root 'src\OllamaDesktop.App\MainWindow.xaml'
    $xamlContent = Get-Content -LiteralPath $xamlPath -Raw -Encoding UTF8
    try { $null = [xml]$xamlContent }
    catch { throw 'Разметка главного окна повреждена; выпуск остановлен.' }
    $handlers = [regex]::Matches($xamlContent, '(?:\s(?:Click|Loaded|Drop|PreviewDragOver|PreviewKeyDown|Closing|ScrollViewer\.ScrollChanged))="(On[A-Za-z0-9]+)"')
    $code = Get-Content -LiteralPath (Join-Path $script:Root 'src\OllamaDesktop.App\MainWindow.xaml.cs') -Raw -Encoding UTF8
    foreach ($match in $handlers) {
        $method = [regex]::Escape([string]$match.Groups[1].Value)
        if ($code -notmatch ('\b' + $method + '\s*\(')) { throw ('Для события UI отсутствует обработчик: ' + $method) }
    }
    return $expected
}
function Assert-TestReport([string]$ReportPath) {
    $expected = Assert-ReleaseContracts
    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) { throw 'Нет отчёта тестов. Сначала выполните сборку.' }
    $report = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.version -ne $script:Version -or [int]$report.count -ne $expected -or [int]$report.failed -ne 0 -or
        @($report.tests).Count -ne $expected -or
        @($report.tests | Where-Object { $_.passed -ne $true }).Count -ne 0) {
        throw ('Отчёт тестов не соответствует версии ' + $script:Version + ' и требованию ' + $expected + '/' + $expected + '. Упаковка запрещена.')
    }
    $names = @($report.tests | ForEach-Object { $_.name })
    if (@($names | Select-Object -Unique).Count -ne $expected -or @($names | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw 'Отчёт тестов содержит повторяющиеся или пустые имена.'
    }
    return $report
}
