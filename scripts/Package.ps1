[CmdletBinding()]
param([string]$Name = 'TH12COOP-Lockstep-Windows')
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[A-Za-z0-9_-]+$') { throw 'Use a simple archive name.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$distRoot = Join-Path $projectRoot 'dist'
foreach ($file in @('TH12Coop.exe','TH12Coop.dll','th12coop-native.dll','injector.exe','th12-online.cfg','coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $distRoot $file))) { throw 'Run build.ps1 -SelfContained first.' }
}
$stage = Join-Path $projectRoot ('build\package-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stage $Name
$packageDist = Join-Path $packageRoot 'dist'
[IO.Directory]::CreateDirectory($packageDist) | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $distRoot -File) {
    if ($file.Name -like '*-test-*' -or $file.Name -like '*-test.*' -or $file.Name -like '*-results.*' -or $file.Name -like '*-smoke*' -or $file.Extension -eq '.pdb') { continue }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageDist $file.Name)
}
foreach ($directory in Get-ChildItem -LiteralPath $distRoot -Directory) {
    Copy-Item -LiteralPath $directory.FullName -Destination (Join-Path $packageDist $directory.Name) -Recurse
}
foreach ($nameToCopy in @('README.md','Start.cmd','Offline.cmd','build.ps1')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $nameToCopy) -Destination (Join-Path $packageRoot $nameToCopy)
}
foreach ($relative in @('src','scripts','docs','licenses','profiles','verification')) {
    $sourceRoot = Join-Path $projectRoot $relative
    if (-not (Test-Path -LiteralPath $sourceRoot)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        $pathWithinRoot = $file.FullName.Substring($projectRoot.TrimEnd('\').Length + 1)
        if ($pathWithinRoot -match '\\(bin|obj|__pycache__)\\') { continue }
        $target = Join-Path $packageRoot $pathWithinRoot
        [IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$releaseRoot = Join-Path $projectRoot 'release'
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$archive = Join-Path $releaseRoot ($Name + '.zip')
if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
Compress-Archive -LiteralPath $packageRoot -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Host 'Package contains no original game assets. Both players prepare their own supported game copy.'
