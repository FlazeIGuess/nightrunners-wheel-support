using System;
using HarmonyLib;
using NightRunners.WheelSupport.Game;

namespace NightRunners.WheelSupport.Patches
{
    // Primary injection point: postfix on CarLocalCustom.Inputs().
    // This is the only per-frame writer of the RCC car inputs (RCC_CarControllerV3 itself no
    // longer has an Inputs() method). We only overwrite the channels for which a wheel axis is
    // configured, and only on the player car.
    [HarmonyPatch(typeof(CarLocalCustom), "Inputs")]
    public static class CarLocalCustomInputsPatch
    {
        public static void Postfix(CarLocalCustom __instance)
        {
            try
            {
                if (!ModContext.InputInjectionEnabled) return;

                var car = __instance.rcc;
                if (car == null) return;

                var player = PlayerCar.Get();
                if (player == null || car.Pointer != player.Pointer) return; // player car only

                InputInjector.Apply(car);
            }
            catch (Exception e)
            {
                ModLog.DebugThrottled("inputpatch", UnityEngine.Time.realtimeSinceStartup, 2f,
                    $"Inputs postfix error: {e.Message}");
            }
        }
    }

    // Secondary injection point for Clutch & Neutral: postfix on RCC_CarControllerV3.Clutch().
    // RCC recalculates clutchInput in FixedUpdate() based on its internal helper; this postfix
    // guarantees our manual clutch pedal and H-shifter neutral override the game's automatic values.
    [HarmonyPatch(typeof(RCC_CarControllerV3), "Clutch")]
    public static class RCCClutchPatch
    {
        public static void Postfix(RCC_CarControllerV3 __instance)
        {
            try
            {
                if (!ModContext.InputInjectionEnabled) return;

                var player = PlayerCar.Get();
                if (player == null || __instance.Pointer != player.Pointer) return;

                if (ModContext.ForceNeutral)
                {
                    __instance.NGear = true;
                    __instance.clutchInput = 1f;
                }
                else if (ModContext.InjectClutch)
                {
                    // Physical clutch pedal overrides auto-clutch whenever depressed
                    if (ModContext.AxClutch > 0.05f)
                    {
                        __instance.clutchInput = Math.Max(__instance.clutchInput, ModContext.AxClutch);
                    }
                }
            }
            catch (Exception e)
            {
                ModLog.DebugThrottled("clutchpatch", UnityEngine.Time.realtimeSinceStartup, 2f,
                    $"Clutch postfix error: {e.Message}");
            }
        }
    }
}
