$ErrorActionPreference = 'Stop'
$projectDir = Split-Path $PSScriptRoot -Parent
$distDir = Join-Path $projectDir 'dist'
$packageName = 'AILimits-v0.4.2-win-x64'
$packageDir = Join-Path $distDir ($packageName + '-' + [Guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Force $distDir | Out-Null
$buildPath = Join-Path $distDir ($packageName + '.exe')
& (Join-Path $PSScriptRoot 'build.ps1') -OutputPath $buildPath
$test = Start-Process -FilePath $buildPath -ArgumentList '--test' -WindowStyle Hidden -Wait -PassThru
if ($test.ExitCode -ne 0) { throw 'Release checks failed' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($buildPath).FileVersion -ne '0.4.2.0') { throw 'Unexpected release version' }

New-Item -ItemType Directory -Force $distDir | Out-Null
New-Item -ItemType Directory (Join-Path $packageDir 'bin') | Out-Null
New-Item -ItemType Directory (Join-Path $packageDir 'scripts') | Out-Null
New-Item -ItemType Directory (Join-Path $packageDir 'assets') | Out-Null
Copy-Item -LiteralPath $buildPath -Destination (Join-Path $packageDir 'bin\AILimits.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $packageDir 'scripts')
Copy-Item -LiteralPath (Join-Path $projectDir 'README.md') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $projectDir 'assets\ailimits-icon.png') -Destination (Join-Path $packageDir 'assets')
$releaseExe = Join-Path $distDir ($packageName + '.exe')
$releaseZip = Join-Path $distDir ($packageName + '.zip')
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $releaseZip -Force
$checksums = foreach ($file in @($releaseExe, $releaseZip)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
}
$checksums | Set-Content -LiteralPath (Join-Path $distDir 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Release checks passed. Artifacts: $distDir"
