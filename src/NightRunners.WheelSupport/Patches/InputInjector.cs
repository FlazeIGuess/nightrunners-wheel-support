using System;
using NightRunners.WheelSupport.Game;

namespace NightRunners.WheelSupport.Patches
{
    // Shared injection logic, called from two paths:
    //  1) Harmony postfix on CarLocalCustom.Inputs()  (semantically correct, if not inlined)
    //  2) WheelRuntime.LateUpdate()                    (inlining-immune; wins if Inputs runs in the Update cycle)
    // Both write the same values -> idempotent, no conflict. So injection works regardless of
    // whether/where IL2CPP inlined Inputs().
    public static class InputInjector
    {
        public static void Apply(RCC_CarControllerV3 car)
        {
            if (car == null) return;
            if (!ModContext.InputInjectionEnabled) return;
            // Direct guard against live deactivation (in case the flag lags one frame behind).
            var cfg = ModContext.Config;
            if (cfg == null || !cfg.Enabled.Value || !cfg.UseWheelInput.Value) return;
            var w = ModContext.Wheel;
            if (w == null || !w.Available || w.Lost) return;

            try
            {
                if (ModContext.InjectSteer)
                {
                    car.steerInput = ModContext.AxSteer;
                    ModContext.LastInjectedSteer = ModContext.AxSteer;
                }
                if (ModContext.InjectThrottle) car.gasInput = ModContext.AxThrottle;
                if (ModContext.InjectBrake) car.brakeInput = ModContext.AxBrake;
                if (ModContext.InjectClutch) car.clutchInput = ModContext.AxClutch;
                if (ModContext.ForceNeutral)
                {
                    car.NGear = true;
                    car.clutchInput = 1f;
                }
                if (ModContext.InjectHandbrake) car.handbrakeInput = ModContext.AxHandbrake;
                if (ModContext.InjectNos) car.boostInput = ModContext.AxNos;
            }
            catch (Exception e)
            {
                ModLog.DebugThrottled("inject", UnityEngine.Time.realtimeSinceStartup, 2f,
                    $"Injection failed: {e.Message}");
            }
        }

        // Injection on the current player car (for the LateUpdate path).
        public static void ApplyToPlayer()
        {
            if (!ModContext.InputInjectionEnabled) return;
            var car = PlayerCar.Get();
            Apply(car);
        }
    }
}
