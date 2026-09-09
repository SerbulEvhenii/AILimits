param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path $PSScriptRoot -Parent
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force (Join-Path $projectDir 'bin') | Out-Null
if (!$OutputPath) { $OutputPath = Join-Path $projectDir 'bin\AILimits.exe' }
& (Join-Path $frameworkDir 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/win32icon:$projectDir\assets\ailimits.ico" "/out:$OutputPath" "/resource:$projectDir\assets\codex-dark.png,codex-dark.png" "/resource:$projectDir\assets\codex-light.png,codex-light.png" "/resource:$projectDir\assets\ailimits.ico,ailimits.ico" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$frameworkDir\WPF\UIAutomationClient.dll" "/reference:$frameworkDir\WPF\UIAutomationTypes.dll" "/reference:$frameworkDir\WPF\WindowsBase.dll" "$projectDir\src\AILimits.cs" "$projectDir\src\Settings.cs" "$projectDir\src\Taskbar.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
