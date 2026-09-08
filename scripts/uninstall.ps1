$ErrorActionPreference = 'Stop'
$exePath = Join-Path $env:LOCALAPPDATA 'AILimits\AILimits.exe'
foreach ($process in (Get-Process AILimits -ErrorAction SilentlyContinue)) {
    if ($process.Path -eq $exePath) { Stop-Process -Id $process.Id }
}
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'AILimits.lnk'
if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
if (Test-Path -LiteralPath $exePath) { Remove-Item -LiteralPath $exePath }
Write-Output 'AILimits stopped; executable and startup shortcut removed.'
