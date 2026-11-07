. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Assert-InstalledRelease
Start-Process -FilePath (Join-Path $InstallDir 'OllamaDesktop.exe')
