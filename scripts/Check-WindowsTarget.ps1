# Offline OS compatibility report. No elevation, network, settings changes or secrets.
$ErrorActionPreference = 'Stop'
$os = Get-CimInstance -ClassName Win32_OperatingSystem
$computer = Get-CimInstance -ClassName Win32_ComputerSystem
$build = [int]$os.BuildNumber
$isClient = [int]$os.ProductType -eq 1
$is64 = [Environment]::Is64BitOperatingSystem
Write-Host ('Windows: ' + $os.Caption)
Write-Host ('Version: ' + $os.Version + ', build ' + $build)
Write-Host ('Architecture: ' + $os.OSArchitecture)
Write-Host ('System: ' + $computer.SystemType)
Write-Host ('User environment: ' + $(if($is64){'64-bit'}else{'32-bit'}))
if (-not $isClient -or $build -lt 10240) {
    Write-Host 'Result: UNAVAILABLE - requires Windows 10 or Windows 11 client OS.' -ForegroundColor Red
} elseif (-not $is64) {
    Write-Host 'Result: UNAVAILABLE - current package contains x64 application only.' -ForegroundColor Red
} elseif ($os.OSArchitecture -match 'ARM' -or $computer.SystemType -match 'ARM') {
    if ($build -ge 22000) {
        Write-Host 'Result: EXPERIMENTAL - Windows 11 ARM64 x64 emulation. Native dependencies have not been tested.' -ForegroundColor Yellow
    } else {
        Write-Host 'Result: UNAVAILABLE - Windows 10 ARM64 x64 emulation is not supported by this package.' -ForegroundColor Red
    }
} elseif ($build -lt 14393) {
    Write-Host 'Result: EXPERIMENTAL - installer permits Windows 10 1507/1511; .NET 10 vendor support is absent.' -ForegroundColor Yellow
} else {
    Write-Host 'Result: INSTALLER_ELIGIBLE - Windows 10/11 x64, runtime compatibility must still be checked.' -ForegroundColor Green
    if ($build -lt 22000) { Write-Host 'Note: Microsoft support for .NET 10 on Windows 10 is limited to named LTSC/Enterprise editions.' }
}
