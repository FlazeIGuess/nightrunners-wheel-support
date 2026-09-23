using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using NightRunners.WheelSupport.Config;
using NightRunners.WheelSupport.Ffb;
using NightRunners.WheelSupport.Input;
using UnityEngine;

namespace NightRunners.WheelSupport
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class WheelSupportPlugin : BasePlugin
    {
        public const string PluginGuid = "com.flaze.nightrunners.wheelsupport";
        public const string PluginName = "NIGHT-RUNNERS Wheel Support";
        public const string PluginVersion = "0.1.0-alpha";

        public override void Load()
        {
            try
            {
                // Persist every config change to disk immediately (belt), on top of the explicit
                // Save() the wizard/menu call on completion/close (suspenders). This is what makes
                // in-game changes survive a game restart.
                try { Config.SaveOnConfigSet = true; } catch { }

                var cfg = new WheelConfig();
                cfg.Bind(Config);
                ModLog.Init(Log, cfg.VerboseLogging.Value);

                ModLog.Info($"{PluginName} v{PluginVersion} starting.");

                if (!cfg.Enabled.Value)
                {
                    ModLog.Info("Mod is disabled via config (Enabled=false). Nothing to do.");
                    return;
                }

                ModContext.Config = cfg;
                ModContext.Wheel = new DirectInputWheel();
                ModContext.Ffb = new ForceFeedbackController(cfg, ModContext.Wheel);
                ModContext.ComputeInjectionFlags();

                // Apply the Harmony patches SEPARATELY: a broken target must not take the other
                // one down with it (input injection and crash FFB are independent). Errors here
                // must NOT crash the game.
                var harmony = new Harmony(PluginGuid);
                PatchClass(harmony, typeof(Patches.CarLocalCustomInputsPatch), "CarLocalCustom.Inputs (input injection)");
                PatchClass(harmony, typeof(Patches.RCCClutchPatch), "RCC_CarControllerV3.Clutch (manual clutch & neutral)");
                PatchClass(harmony, typeof(Patches.OnCollisionEnterPatch), "RCC_CarControllerV3.OnCollisionEnter (crash FFB)");

                // Register and attach the Il2Cpp runtime component
                try
                {
                    ClassInjector.RegisterTypeInIl2Cpp<WheelRuntime>();
                    var go = new GameObject("NR_WheelRuntime");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.AddComponent<WheelRuntime>();
                    ModLog.Info("WheelRuntime active. Wheel detection runs at game start.");
                }
                catch (Exception e)
                {
                    ModLog.Error($"WheelRuntime could not be started: {e}");
                }
            }
            catch (Exception e)
            {
                // Absolutely nothing may take the game down.
                try { Log.LogError($"Load() failed: {e}"); } catch { }
            }
        }

        private static void PatchClass(Harmony harmony, Type patchClass, string desc)
        {
            try
            {
                harmony.CreateClassProcessor(patchClass).Patch();
                ModLog.Info($"Patch applied: {desc}");
            }
            catch (Exception e)
            {
                ModLog.Warn($"Patch failed (not critical, the rest keeps running): {desc} - {e.Message}");
            }
        }
    }
}
