$ErrorActionPreference = 'Stop'
$projectDir = Split-Path $PSScriptRoot -Parent
$installDir = Join-Path $env:LOCALAPPDATA 'AILimits'
$exePath = Join-Path $installDir 'AILimits.exe'
$buildPath = Join-Path $projectDir 'bin\AILimits.exe'
foreach ($process in (Get-Process AILimits -ErrorAction SilentlyContinue)) {
    if ($process.Path -eq $exePath -or $process.Path -eq $buildPath) {
        Stop-Process -Id $process.Id
        if (-not $process.WaitForExit(5000)) { throw 'AILimits did not stop in time' }
    }
}
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item -LiteralPath $buildPath -Destination $exePath -Force
$startupDir = [Environment]::GetFolderPath('Startup')
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startupDir 'AILimits.lnk'))
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Codex quota indicator embedded beside Windows Widgets'
$shortcut.Save()
Start-Process -FilePath $exePath -WindowStyle Hidden
Write-Output "Installed and started: $exePath"
