using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

// Install the production prefixes/postfixes against the actual game methods.
// No patched game method is executed, and no Unity scene or save is opened.
class NativeCreaturePatchContract
{
    static string managed, harmonyDir, buildDir;
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    static void Main(string[] args)
    {
        managed = args[0]; harmonyDir = args[2]; buildDir = Path.GetDirectoryName(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request) {
            string name = new AssemblyName(request.Name).Name + ".dll";
            foreach (string folder in new string[] { managed, harmonyDir, buildDir })
            { string path = Path.Combine(folder, name); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        try { Run(args[1]); }
        catch (Exception e) { Console.WriteLine("FAIL native patch installation: " + e.GetType().Name + " " + e.GetBaseException().Message); Environment.ExitCode = 1; }
    }
    static void Run(string modPath)
    {
        Assembly game = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        Assembly mod = Assembly.LoadFrom(modPath);
        Type patchType = mod.GetType("RuinaNet.BattleSyncPatches", true);
        string id = "yg.ruinanet.native-creature-contract";
        Harmony harmony = new Harmony(id);
        string[][] targets = {
            new string[] { "UI.UIController", "OnClickStartCreatureStage", "CreatureFirst_Pre", "" },
            new string[] { "UI.UICreatureRebattleNumberSlot", "<OnPointerClick>b__17_0", "CreatureReplay_Pre", "" },
            new string[] { "StageController", "EndBattlePhase_creature", "CreatureEnd_Pre", "" },
            new string[] { "UI.UIController", "EnterFinalBattle", "CreatureFinal_Pre", "" },
            new string[] { "StageController", "GameOver", "", "CreatureGameOver_Post" },
            new string[] { "GlobalGameManager", "LoadBattleScene", "LoadBattleScene_Pre", "" },
            new string[] { "UI.UIController", "PrepareBattle", "PrepareBattle_Pre", "PrepareBattle_Post" }
        };
        try
        {
            foreach (string[] target in targets)
            {
                MethodInfo original = AccessTools.Method(game.GetType(target[0], true), target[1]);
                Check(original != null, "native target exists: " + target[0] + "." + target[1]);
                HarmonyMethod prefix = target[2] == "" ? null : new HarmonyMethod(AccessTools.Method(patchType, target[2]));
                HarmonyMethod postfix = target[3] == "" ? null : new HarmonyMethod(AccessTools.Method(patchType, target[3]));
                harmony.Patch(original, prefix, postfix);
                Patches info = Harmony.GetPatchInfo(original);
                Check(info != null && info.Owners.Contains(id), "production patch installs: " + target[1]);
            }
            Console.WriteLine("PASS: " + checks + " native creature hook contracts using actual game + production patch methods.");
        }
        finally { harmony.UnpatchSelf(); }
    }
}
