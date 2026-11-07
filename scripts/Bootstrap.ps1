. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Environment
Initialize-ToolsDirectory
if (Get-VerifiedSystemSdk) {
    Set-BuildEnvironment
    Write-Host 'Используется уже установленный подписанный SDK 10.0.403.' -ForegroundColor Green
    return
}
$lock = $null
try {
    $lock = [IO.File]::Open((Join-Path $Tools '.sdk.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $sdkVersion = '10.0.403'
    $sdkRoot = Join-Path $Tools ('dotnet-' + $sdkVersion)
    $dotnet = Join-Path $sdkRoot 'dotnet.exe'
    $receiptPath = Join-Path $sdkRoot '.oda-sdk.json'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        if (Test-Path -LiteralPath $sdkRoot) { throw 'Неизвестная/незавершённая папка SDK. Она не будет перезаписана.' }
        Write-Host ('Подготовка SDK {0} в короткой папке: {1}' -f $sdkVersion, $sdkRoot)
        $metadata = Join-Path $Tools 'dotnet-releases.json'
        Get-OfficialFile 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' $metadata
        $releases = Get-Content -LiteralPath $metadata -Raw -Encoding UTF8 | ConvertFrom-Json
        $sdk = $null
        foreach ($release in $releases.releases) {
            $candidates = @($release.sdk)
            if ($release.PSObject.Properties.Name -contains 'sdks') { $candidates += @($release.sdks) }
            foreach ($candidate in $candidates) { if ($null -ne $candidate -and $candidate.version -eq $sdkVersion) { $sdk = $candidate; break } }
            if ($null -ne $sdk) { break }
        }
        if ($null -eq $sdk) { throw 'Закреплённый SDK отсутствует в официальном каталоге. Подмена версии запрещена.' }
        $file = @($sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') }) | Select-Object -First 1
        if ($null -eq $file -or $file.hash -notmatch '^[0-9a-fA-F]{128}$') { throw 'Нет корректного SHA-512 для SDK.' }
        $cacheArchive = Join-Path $Tools ('dotnet-sdk-' + $sdkVersion + '-win-x64.zip')
        $legacyArchive = Join-Path $LegacyTools ('dotnet-sdk-' + $sdkVersion + '-win-x64.zip')
        $archive = $null
        foreach ($candidate in @($cacheArchive, $legacyArchive)) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Assert-PlainPath $candidate
                Write-Host 'Проверка SHA-512 уже скачанного SDK…'
                if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA512).Hash -ne $file.hash) {
                    throw 'Контрольная сумма сохранённого SDK не совпадает. Неизвестный файл не удаляется; запуск запрещён.'
                }
                $archive = $candidate
                Write-Host 'Используется уже скачанный SDK. Повторная загрузка не нужна.' -ForegroundColor Green
                break
            }
        }
        if ($null -eq $archive) {
            $archive = $cacheArchive
            Write-Host 'Сохранённый ZIP SDK не найден. Загрузка из официального источника Microsoft…'
            Get-OfficialFile $file.url $archive
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash) { throw 'SHA-512 SDK не совпадает. Запуск запрещён.' }
        }
        # Both extraction AND final paths must fit Windows PowerShell 5.1's legacy APIs.
        $staging = Join-Path $Tools ('s-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
        $null = Get-SafeZipPlan $archive $sdkRoot
        try {
            Expand-SafeZip $archive $staging
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $staging 'dotnet.exe')
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Подпись Microsoft у dotnet.exe не подтверждена.' }
            [pscustomobject]@{ productId='OllamaDesktopAssistant'; sdk=$sdkVersion; sha512=$file.hash; rid='win-x64' } |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging '.oda-sdk.json') -Encoding UTF8
            Move-Item -LiteralPath $staging -Destination $sdkRoot
        } finally {
            # Only this invocation's fresh, preflight-validated staging directory is removable.
            if (Test-Path -LiteralPath $staging) {
                try { Assert-PlainPath $staging; Remove-Item -LiteralPath $staging -Recurse -Force }
                catch { Write-Warning 'Временная папка SDK сохранена; чужие файлы и старый .tools не удалялись.' }
            }
        }
    }
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'SDK не имеет проверяемой метки установки. Запуск остановлен.' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($receipt.productId -ne 'OllamaDesktopAssistant' -or $receipt.sdk -ne $sdkVersion -or $receipt.rid -ne 'win-x64' -or $receipt.sha512 -notmatch '^[0-9a-fA-F]{128}$') { throw 'Неизвестная установка SDK.' }
    Set-BuildEnvironment
    Push-Location $Root
    try {
        $signature = Get-AuthenticodeSignature -LiteralPath $Dotnet
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Подпись локального SDK не подтверждена.' }
        $actual = (& $Dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actual -ne $sdkVersion) { throw ('Версия SDK не совпадает с закреплённой ' + $sdkVersion) }
    } finally { Pop-Location }
    Write-Host 'SDK готов.' -ForegroundColor Green
} finally { if ($null -ne $lock) { $lock.Dispose() } }
