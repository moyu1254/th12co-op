[CmdletBinding()]
param(
    [string]$OriginalGame = 'C:\Program Files (x86)\上海アリス幻樂団\東方星蓮船',
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$sourceRoot = (Resolve-Path -LiteralPath $OriginalGame).Path.TrimEnd('\', '/')

$profiles = @{
    'e2939ba3f92e427216a619191a682a8197cc59a64eab99302a7b98811d6120a9' = @{
        id = 'th12.v1.00a'; version = '1.00a'; distribution = 'retail'; folder = 'game'
        dataHash = '086cd832c73fe170f9e1de467beabe53b73caf04bf94905acb081e3a70137ab7'
    }
    'eb275a8c285fd8450fa5ac79fa730a759aeca8b2904aeb16d30e65859372a459' = @{
        id = 'th12.v1.00b-steam'; version = '1.00b'; distribution = 'steam'; folder = 'game-steam-1.00b'
        dataHash = 'dca82df072ba51c4b74c3d41ea1cbdcb91f709ce7cfea23599ee52caa34f2d72'
    }
    'd8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628' = @{
        id = 'th12.v1.00b'; version = '1.00b'; distribution = 'retail'; folder = 'game-retail-1.00b'
        dataHash = 'dca82df072ba51c4b74c3d41ea1cbdcb91f709ce7cfea23599ee52caa34f2d72'
    }
}
$requiredFiles = @('th12.exe', 'th12.dat', 'thbgm.dat')
foreach ($name in $requiredFiles) {
    $path = Join-Path $sourceRoot $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing game file: $path"
    }
}
$exeHash = (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'th12.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $profiles.ContainsKey($exeHash)) {
    throw "Unrecognized TH12 executable SHA-256: $exeHash. No files were copied."
}
$profile = $profiles[$exeHash]
if (-not $Destination) { $Destination = Join-Path $projectRoot $profile.folder }
$targetRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
if (-not $targetRoot.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The game copy must be inside this project. Installed game folders are never modified.'
}
if ($targetRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $targetRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $sourceRoot.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and destination must be separate folders.'
}
$ancestor = $targetRoot
while ($ancestor.Length -ge $projectRoot.Length) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Destination cannot pass through a junction or symbolic link: $ancestor"
        }
    }
    $ancestor = Split-Path $ancestor -Parent
}
$optionalDependencies = Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object {
    $_.Name -eq 'd3dx9_40.dll'
} | Select-Object -ExpandProperty Name
$fileNames = @($requiredFiles) + @($optionalDependencies)

# Hash before copying, and refuse a mixed-version folder before replacing any file.
$files = foreach ($name in $fileNames) {
    $sourceFile = Join-Path $sourceRoot $name
    [ordered]@{
        name = $name
        length = (Get-Item -LiteralPath $sourceFile).Length
        sha256 = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
if (($files | Where-Object name -eq 'th12.dat').sha256 -ne $profile.dataHash) {
    throw 'Game data does not match this executable profile. No files were copied.'
}
if (($files | Where-Object name -eq 'thbgm.dat').sha256 -ne '052a6f448a99ac829e23e4e724400129bd04a7201667a7447237ed7cd5ad5793') {
    throw 'Unrecognized or modified TH12 music archive. No files were copied.'
}
$existingExe = Join-Path $targetRoot 'th12.exe'
if (Test-Path -LiteralPath $existingExe) {
    $existingHash = (Get-FileHash -LiteralPath $existingExe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($existingHash -ne $exeHash) { throw 'Destination contains a different executable. Choose a separate destination.' }
}
$missingFiles = @()
foreach ($file in $files) {
    $targetFile = Join-Path $targetRoot $file.name
    if (Test-Path -LiteralPath $targetFile -PathType Leaf) {
        if ((Get-Item -LiteralPath $targetFile -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Destination file cannot be a symbolic link: $targetFile" }
        if ((Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Destination contains different game data: $targetFile. Use a separate destination; existing data was preserved."
        }
    } else {
        $missingFiles += $file
    }
}
# Stage and verify every missing file before publishing any game file. Existing
# game binaries/data are never replaced, even on an interrupted previous setup.
$stageRoot = Join-Path $projectRoot ('.prepare-' + [guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($stageRoot) | Out-Null
    foreach ($file in $missingFiles) {
        $stagedFile = Join-Path $stageRoot $file.name
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file.name) -Destination $stagedFile
        if ((Get-FileHash -LiteralPath $stagedFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Copied file failed SHA-256 verification: $($file.name)"
        }
    }
    [IO.Directory]::CreateDirectory($targetRoot) | Out-Null
    foreach ($file in $missingFiles) {
        [IO.File]::Move((Join-Path $stageRoot $file.name), (Join-Path $targetRoot $file.name))
    }
} finally {
    # Only known files inside this invocation's private directory are removed.
    foreach ($file in $missingFiles) {
        $stagedFile = Join-Path $stageRoot $file.name
        if (Test-Path -LiteralPath $stagedFile -PathType Leaf) { Remove-Item -LiteralPath $stagedFile }
    }
    if (Test-Path -LiteralPath $stageRoot -PathType Container) { Remove-Item -LiteralPath $stageRoot }
}

# Keep the user's controls, but let TH12 create a new score file and replay folder.
$sourceConfig = Join-Path $sourceRoot 'th12.cfg'
$targetConfig = Join-Path $targetRoot 'th12.cfg'
foreach ($name in @('th12.cfg','steam_appid.txt')) {
    $path = Join-Path $targetRoot $name
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Destination file cannot be a symbolic link: $path" }
}
if ((Test-Path -LiteralPath $sourceConfig -PathType Leaf) -and -not (Test-Path -LiteralPath $targetConfig)) {
    Copy-Item -LiteralPath $sourceConfig -Destination $targetConfig
}
if ($profile.distribution -eq 'steam') {
    $appIdPath = Join-Path $targetRoot 'steam_appid.txt'
    if (Test-Path -LiteralPath $appIdPath) {
        if ([IO.File]::ReadAllText($appIdPath).Trim() -ne '1100160') { throw 'Steam App ID does not match TH12.' }
    } else {
        [IO.File]::WriteAllText($appIdPath, '1100160', [Text.Encoding]::ASCII)
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    profile = $profile.id
    gameVersion = $profile.version
    distribution = $profile.distribution
    sourceDirectory = $sourceRoot
    executableSha256 = $exeHash
    files = @($files)
}
$manifestPath = Join-Path $targetRoot 'game-profile.json'
$manifestTemp = Join-Path $targetRoot ('game-profile.' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestTemp -Encoding UTF8
    if (Test-Path -LiteralPath $manifestPath) {
        [IO.File]::Replace($manifestTemp, $manifestPath, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [IO.File]::Move($manifestTemp, $manifestPath)
    }
} finally {
    if (Test-Path -LiteralPath $manifestTemp -PathType Leaf) { Remove-Item -LiteralPath $manifestTemp }
}
Write-Host "Verified isolated TH12 $($profile.version) ($($profile.distribution)): $targetRoot"
Write-Host 'Scores and replays were not copied; existing MOD settings were preserved.'
if ($profile.distribution -eq 'steam') {
    Write-Host 'Steam must be running and signed in to an account entitled to TH12. The original Steam bootstrap remains enabled.'
}
