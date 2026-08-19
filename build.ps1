$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework 4.x C# compiler was not found.'
}

& $compiler `
    /nologo `
    /optimize+ `
    /target:winexe `
    /platform:anycpu `
    /win32icon:"$projectDir\PulseFloat.ico" `
    /out:"$projectDir\PulseFloat.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$projectDir\PulseFloat.cs"

if ($LASTEXITCODE -ne 0) {
    throw 'Build failed.'
}

Write-Host "Built $projectDir\PulseFloat.exe"
