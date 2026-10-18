param([string]$ManagedPath=$env:RUINA_MANAGED, [string]$HarmonyPath=$env:RUINA_HARMONY)
$ErrorActionPreference = 'Stop'
$managed = $ManagedPath
if (!$managed -or !$HarmonyPath) { throw 'Native checks require ManagedPath and HarmonyPath' }
$refs = @()
foreach ($file in Get-ChildItem -LiteralPath $managed -Filter *.dll) {
    if ($file.BaseName -notmatch '^(mscorlib|System|Accessibility|Microsoft|netstandard|WindowsBase|PresentationCore|PresentationFramework|WindowsFormsIntegration)') {
        $refs += ('/r:' + $file.FullName)
    }
}
$out = Join-Path $PSScriptRoot 'build\NativeLoadoutContract.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /utf8output /codepage:65001 /out:$out $refs `
    (Join-Path $PSScriptRoot 'tests\NativeLoadoutContract.cs') `
    (Join-Path $PSScriptRoot 'src\CorePageReplica.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native loadout contract build failed' }
& $out $managed
if ($LASTEXITCODE -ne 0) { throw 'Native loadout contract run failed' }
$hookOut = Join-Path $PSScriptRoot 'build\NativeCreaturePatchContract.exe'
${harmony} = $HarmonyPath
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /utf8output /codepage:65001 /out:$hookOut "/r:$harmony" `
    (Join-Path $PSScriptRoot 'tests\NativeCreaturePatchContract.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native creature hook contract build failed' }
# Allow this test executable to use the known Harmony dependency without
# modifying its downloaded-file marker or any game/system configuration.
Set-Content -LiteralPath ($hookOut + '.config') -Encoding UTF8 -Value '<configuration><runtime><loadFromRemoteSources enabled="true" /></runtime></configuration>'
& $hookOut $managed (Join-Path $PSScriptRoot 'build\RuinaNet.dll') (Split-Path -Parent $harmony)
if ($LASTEXITCODE -ne 0) { throw 'Native creature hook contract run failed' }
