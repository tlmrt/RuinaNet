param([switch]$SkipNative, [string]$ManagedPath=$env:RUINA_MANAGED, [string]$HarmonyPath=$env:RUINA_HARMONY)
$null=New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'build') -Force
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'build\SyncRegression.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /utf8output /codepage:65001 /out:$out `
  (Join-Path $root 'tests\SyncRegression.cs') `
  (Join-Path $root 'src\RoundGate.cs') `
  (Join-Path $root 'src\RoundCommitBarrier.cs') `
  (Join-Path $root 'src\ReliableChannel.cs') `
  (Join-Path $root 'src\SyncAuthority.cs')
if ($LASTEXITCODE -ne 0) { throw 'Regression build failed' }
& $out
if ($LASTEXITCODE -ne 0) { throw 'Regression run failed' }
$clashOut = Join-Path $root 'build\ClashRegression.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /utf8output /codepage:65001 /out:$clashOut `
  (Join-Path $root 'tests\ClashRegression.cs') `
  (Join-Path $root 'src\ClashSync.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clash regression build failed' }
& $clashOut
if ($LASTEXITCODE -ne 0) { throw 'Clash regression run failed' }
$loadoutOut = Join-Path $root 'build\LoadoutRegression.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /utf8output /codepage:65001 /out:$loadoutOut `
  (Join-Path $root 'tests\LoadoutRegression.cs') `
  (Join-Path $root 'src\Loadout.cs') `
  (Join-Path $root 'src\LoadoutUi.cs') `
  (Join-Path $root 'src\CreatureBarrier.cs') `
  (Join-Path $root 'src\CorePageReplica.cs')
if ($LASTEXITCODE -ne 0) { throw 'Loadout regression build failed' }
& $loadoutOut
if ($LASTEXITCODE -ne 0) { throw 'Loadout regression run failed' }
$creatureOut = Join-Path $root 'build\CreatureRegression.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /utf8output /codepage:65001 /out:$creatureOut `
  (Join-Path $root 'tests\CreatureRegression.cs') `
  (Join-Path $root 'src\CreatureBarrier.cs') `
  (Join-Path $root 'src\CreatureSync.cs')
if ($LASTEXITCODE -ne 0) { throw 'Creature regression build failed' }
& $creatureOut
if ($LASTEXITCODE -ne 0) { throw 'Creature regression run failed' }
& (Join-Path $root 'build-steam-tests.ps1')
if (!$SkipNative) { & (Join-Path $root 'build-native-tests.ps1') -ManagedPath $ManagedPath -HarmonyPath $HarmonyPath }
