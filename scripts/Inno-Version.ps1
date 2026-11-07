# Match an exact official ISCC compiler version, not Windows PE FileVersionInfo.
function Test-InnoCompilerVersion([string]$Output) {
    if ([string]::IsNullOrWhiteSpace($Output)) { return $false }
    $s = $Output.Trim()
    return ($s -match '(?i)(?<![0-9.])7\.1\.0(?:\.0)?(?![0-9.])' -and
        $s -notmatch '(?i)\b7\.1\.(?:[1-9][0-9]*)\b' -and
        $s -notmatch '(?i)\b7\.(?:[02-9])\.')
}
