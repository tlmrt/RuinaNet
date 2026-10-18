param([string]$ManagedPath = $env:RUINA_MANAGED, [string]$HarmonyPath = $env:RUINA_HARMONY)
$ErrorActionPreference = 'Stop'
if (!$ManagedPath -or !(Test-Path -LiteralPath (Join-Path $ManagedPath 'Assembly-CSharp.dll'))) { throw '请通过 -ManagedPath 或 RUINA_MANAGED 指定合法安装的 LibraryOfRuina_Data/Managed' }
if (!$HarmonyPath -or !(Test-Path -LiteralPath $HarmonyPath)) { throw '请通过 -HarmonyPath 或 RUINA_HARMONY 指定 HarmonyX 的 0Harmony.dll' }
$refs = @('/r:' + (Resolve-Path -LiteralPath $HarmonyPath).Path)
foreach ($file in Get-ChildItem -LiteralPath $ManagedPath -Filter *.dll) {
    if ($file.BaseName -notmatch '^(mscorlib|System|Accessibility|Microsoft|netstandard|WindowsBase|PresentationCore|PresentationFramework|WindowsFormsIntegration)') { $refs += ('/r:' + $file.FullName) }
}
$null = New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'build') -Force
$files = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName
$out = Join-Path $PSScriptRoot 'build/RuinaNet.dll'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /utf8output /codepage:65001 /target:library /optimize+ /out:$out $refs $files
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Get-FileHash -LiteralPath $out -Algorithm SHA256