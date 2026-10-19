$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$artifactPath = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
$testExe = Join-Path $artifactPath 'UiSmokeTests.exe'
$sourcePaths = @(Get-ChildItem -LiteralPath (Join-Path $projectPath 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$compilerArgs = @('/nologo', '/target:exe', '/main:UiSmokeTests', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', "/out:$testExe", '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll') + $sourcePaths + @(Join-Path $PSScriptRoot 'UiSmokeTests.cs')
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'UI smoke-test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'UI smoke tests failed.' }
