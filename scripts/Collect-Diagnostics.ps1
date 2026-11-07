. (Join-Path $PSScriptRoot 'Common.ps1')
New-OwnedDirectory $Artifacts
$stage = Join-Path $Artifacts ('diagnostics-stage-' + [Guid]::NewGuid().ToString('N')); New-OwnedDirectory $stage
try {
    $info = [pscustomobject]@{ product='OllamaDesktopAssistant'; version=$Version; utc=[DateTime]::UtcNow.ToString('o'); powershell=$PSVersionTable.PSVersion.ToString(); osVersion=[Environment]::OSVersion.Version.ToString(); process64=[Environment]::Is64BitProcess; sdkPresent=(Test-Path -LiteralPath (Join-Path $Tools 'dotnet\dotnet.exe')); installed=(Test-Path -LiteralPath (Join-Path $InstallDir 'release-installed.json')) }
    $info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'environment.json') -Encoding UTF8
    foreach ($report in @('tests.json','smoke-test.json','bootstrap-tests.json','last-failure.json')) {
        $file = Join-Path $Artifacts $report
        if (Test-Path -LiteralPath $file) { Protect-DiagnosticText (Get-Content -LiteralPath $file -Raw -Encoding UTF8) | Set-Content -LiteralPath (Join-Path $stage $report) -Encoding UTF8 }
    }
    $errors = @()
    foreach ($name in @('build.log','tests.log','packaging.log')) {
        $file = Join-Path $Artifacts $name
        if (Test-Path -LiteralPath $file) {
            foreach ($line in Get-Content -LiteralPath $file -Encoding UTF8 | Select-Object -Last 300) {
                if ($line -match '(?i)(error|ошибк|fail|exception|NU19|CS[0-9]{4}|MSB[0-9]{4}|NETSDK)') {
                    $safe = Protect-DiagnosticText $line
                    $safe = [regex]::Replace($safe, '(?i)(bearer\s+|api[_-]?key\s*[:=]\s*)\S+', '$1[REDACTED]')
                    $safe = [regex]::Replace($safe, '(?i)(https?://)[^\s/@]+:[^\s/@]+@', '$1[REDACTED]@')
                    $errors += $safe
                }
            }
        }
    }
    $errors | Set-Content -LiteralPath (Join-Path $stage 'build-errors-redacted.txt') -Encoding UTF8
    'Включены только техническое окружение, отчёты автоматических тестов и отфильтрованные ошибки сборки. История, ключ, пользовательские вложения и настройки не копировались. Перед отправкой просмотрите архив.' | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding UTF8
    $zip = Join-Path $Artifacts ('OllamaDesktop-Diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6) + '.zip')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    Write-Host ('Диагностический архив: ' + $zip) -ForegroundColor Cyan
} finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force } }
