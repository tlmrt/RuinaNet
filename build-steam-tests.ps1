$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'build\SteamRegression.exe'
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /utf8output /codepage:65001 /out:$out `
    (Join-Path $PSScriptRoot 'tests\SteamRegression.cs') `
    (Join-Path $PSScriptRoot 'src\SteamLink.cs') `
    (Join-Path $PSScriptRoot 'src\NetLink.cs') `
    (Join-Path $PSScriptRoot 'src\ReliableChannel.cs')
if ($LASTEXITCODE -ne 0) { throw 'Steam regression build failed' }
& $out
if ($LASTEXITCODE -ne 0) { throw 'Steam regression failed' }
