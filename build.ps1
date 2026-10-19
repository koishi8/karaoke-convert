$ErrorActionPreference = 'Stop'
$workspacePath = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}
$outputPath = Join-Path $workspacePath 'dist'
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$executablePath = Join-Path $outputPath 'KaraokeConvert.exe'
$sourcePaths = @(Get-ChildItem -LiteralPath (Join-Path $workspacePath 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$compilerArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', "/out:$executablePath", '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', '/win32manifest:app.manifest') + $sourcePaths
Push-Location -LiteralPath $workspacePath
try {
    & $compilerPath @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally {
    Pop-Location
}
Copy-Item -LiteralPath (Join-Path $workspacePath 'app.config') -Destination ($executablePath + '.config') -Force
$releaseFiles = @($executablePath, ($executablePath + '.config'))
foreach ($documentName in @('README.md', 'RELEASE_NOTES.md')) {
    $documentPath = Join-Path $workspacePath $documentName
    if (Test-Path -LiteralPath $documentPath) {
        $copiedPath = Join-Path $outputPath $documentName
        Copy-Item -LiteralPath $documentPath -Destination $copiedPath -Force
        $releaseFiles += $copiedPath
    }
}
$archivePath = Join-Path $outputPath 'KaraokeConvert-Windows.zip'
Compress-Archive -LiteralPath $releaseFiles -DestinationPath $archivePath -Force
$checksumPaths = @($executablePath, ($executablePath + '.config'), $archivePath)
$checksumLines = @($checksumPaths | ForEach-Object {
    $fileHash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    $fileHash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($_)
})
[System.IO.File]::WriteAllLines((Join-Path $outputPath 'SHA256SUMS.txt'), $checksumLines, [System.Text.Encoding]::ASCII)
Write-Host ('Built: ' + $executablePath)
Write-Host ('Packaged: ' + $archivePath)
