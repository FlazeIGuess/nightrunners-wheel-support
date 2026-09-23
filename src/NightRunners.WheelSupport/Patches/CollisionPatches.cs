using System;
using HarmonyLib;
using NightRunners.WheelSupport.Game;

namespace NightRunners.WheelSupport.Patches
{
    // Crash FFB hook: postfix on RCC_CarControllerV3.OnCollisionEnter(Collision).
    // Provides the collision directly -> we derive the strength from relativeVelocity.
    // Only the player car triggers the effect.
    [HarmonyPatch(typeof(RCC_CarControllerV3), "OnCollisionEnter")]
    public static class OnCollisionEnterPatch
    {
        public static void Postfix(RCC_CarControllerV3 __instance, UnityEngine.Collision collision)
        {
            try
            {
                var ffb = ModContext.Ffb;
                if (ffb == null) return;

                var player = PlayerCar.Get();
                if (player == null || __instance.Pointer != player.Pointer) return;

                float intensity = 0f;
                try { intensity = collision.relativeVelocity.magnitude; } catch { }
                if (intensity <= 0f)
                {
                    try { intensity = __instance.speed; } catch { }
                }

                ffb.TriggerCrash(intensity, UnityEngine.Time.realtimeSinceStartup);
            }
            catch (Exception e)
            {
                ModLog.DebugThrottled("crashpatch", UnityEngine.Time.realtimeSinceStartup, 2f,
                    $"OnCollisionEnter postfix error: {e.Message}");
            }
        }
    }
}
