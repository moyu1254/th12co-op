[CmdletBinding()]
param([switch]$RunSelfTest)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$entries = ((& git -C $projectRoot ls-files --stage -z) -join "`n").Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
if ($LASTEXITCODE -ne 0 -or $entries.Count -eq 0) { throw 'Run this check in a Git checkout with tracked files.' }

# Only source, reviewed documentation/results, and one fixed MOD configuration
# belong in Git. Original game assets and compiled diagnostics stay local.
$allowed = '^(?:\.gitignore|\.gitattributes|(?:README|AGENTS|CHANGELOG|SECURITY|CONTRIBUTING|LICENSE|NOTICE)(?:\.(?:md|txt))?|build\.ps1|(?:Start|Offline)\.cmd|\.github/workflows/[^/]+\.ya?ml|src/.+\.(?:cs|csproj|cpp|h)|scripts/[^/]+\.(?:ps1|py)|docs/.+\.md|licenses/[^/]+\.txt|verification/.+\.(?:md|json|txt)|profiles/th12-online\.cfg)$'
$excluded = '(^|/)(?:bin|obj|__pycache__|game(?:-[^/]*)?|sessions|build|dist|release|artifacts|research|\.cache)(/|$)'
$secret = '(?:-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,}|T12L3\.[A-Za-z0-9_-]{86})'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$count = 0
foreach ($entry in $entries) {
    if ($entry -notmatch '^100(?:644|755) [0-9a-f]{40,64} 0\t([^\r\n]+)$') { throw 'Unsupported Git entry: symlinks, submodules and unresolved conflicts are not allowed.' }
    $relative = $Matches[1]
    if ($relative -notmatch $allowed -or $relative -match $excluded -or $relative -match '(^|/)(?:\.\.?|\.env(?:\..*)?|id_rsa|id_ed25519)(/|$)') {
        throw "File is outside the source allowlist: $relative"
    }
    $path = Join-Path $projectRoot $relative
    $file = Get-Item -LiteralPath $path
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked file is not allowed: $relative" }
    if ($file.Length -gt 2MB) { throw "Unexpectedly large source file: $relative" }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($relative -eq 'profiles/th12-online.cfg') {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne '9D3016FDF6F7C1395A399DE7DE0081B0FA766787B4E0C472A2564CA713833A8F') {
            throw 'The fixed online configuration changed; review and update its allowlisted fingerprint.'
        }
    } else {
        if ($bytes -contains 0) { throw "Binary content is not allowed: $relative" }
        try { $content = $utf8.GetString($bytes) } catch { throw "Source must be UTF-8 text: $relative" }
        if ($content -match $secret) { throw "Possible credential or room invite in $relative (value withheld)." }
    }
    if ($relative.EndsWith('.ps1')) {
        $parseErrors = $null; $tokens = $null
        [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count) { throw "PowerShell parse error in ${relative}: $($parseErrors[0].Message)" }
    }
    ++$count
}
Write-Host "PASS repository contents: $count tracked source/documentation files."

if ($RunSelfTest) {
    $launcher = Join-Path $projectRoot 'dist\TH12Coop.exe'
    $report = Join-Path $projectRoot 'dist\launcher-test-results.txt'
    $managedReport = Join-Path $projectRoot 'dist\managed-audit-results.json'
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'Run build.ps1 -SelfContained first.' }
    # A WinExe must be explicitly waited on; PowerShell's direct invocation can
    # otherwise return before the process or its exit status is available.
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    if (Test-Path -LiteralPath $managedReport) { Remove-Item -LiteralPath $managedReport }
    $process = Start-Process -FilePath $launcher -ArgumentList '--self-test' -WorkingDirectory (Split-Path $launcher) -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(150000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'Launcher self-tests exceeded 150 seconds.'
        }
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
        if ($process.ExitCode -ne 0) { throw "Launcher self-tests failed with exit code $($process.ExitCode)." }
        if (-not (Test-Path -LiteralPath $report) -or (Get-Content -LiteralPath $report -Raw) -notmatch '(?m)^PASS [1-9][0-9]* test groups\s*$') {
            throw 'Launcher did not produce a complete self-test report.'
        }
    } finally { $process.Dispose() }
    if (-not (Test-Path -LiteralPath $managedReport)) { throw 'Managed regression report was not created.' }
    $managedResult = Get-Content -LiteralPath $managedReport -Raw | ConvertFrom-Json
    if (-not $managedResult.passed -or $managedResult.failed -ne 0 -or $managedResult.groups -lt 1) { throw 'Managed regressions did not pass.' }
    Write-Host "PASS managed regressions: $($managedResult.groups) groups."
}
