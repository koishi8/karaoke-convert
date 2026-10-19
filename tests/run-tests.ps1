param([string]$FFmpeg = '')
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$artifactPath = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
$testExe = Join-Path $artifactPath 'IntegrationTests.exe'
& $compilerPath /nologo /target:exe /platform:anycpu /optimize+ /utf8output /codepage:65001 "/out:$testExe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll (Join-Path $projectPath 'src\MediaEngine.cs') (Join-Path $PSScriptRoot 'IntegrationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration-test compilation failed.' }
if ([string]::IsNullOrWhiteSpace($FFmpeg)) {
    & $testExe
} else {
    & $testExe $FFmpeg
}
if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed; see tests\artifacts\run-*\results.txt.' }
