using NightRunners.WheelSupport.Config;
using NightRunners.WheelSupport.Ffb;
using NightRunners.WheelSupport.Input;

namespace NightRunners.WheelSupport
{
    // Static access point for the Harmony patches (which have no instance).
    public static class ModContext
    {
        public static WheelConfig Config;
        public static DirectInputWheel Wheel;
        public static ForceFeedbackController Ffb;

        // true when wheel axes should be written into the car inputs
        public static bool InputInjectionEnabled;

        // which channels are injected (depends on axis configuration / device presence)
        public static bool InjectSteer;
        public static bool InjectThrottle;
        public static bool InjectBrake;
        public static bool InjectClutch;
        public static bool InjectHandbrake;
        public static bool InjectNos;

        // current normalized axes (updated every frame by WheelRuntime)
        public static float AxSteer;
        public static float AxThrottle;
        public static float AxBrake;
        public static float AxClutch;
        public static float AxHandbrake;
        public static float AxNos;

        // Transmission & clutch state
        public static bool ForceNeutral;
        public static int CurrentGearDisplay; // -1 = R, 0 = N, 1..6 = Gears

        public static float LastInjectedSteer;

        // set while a wizard/menu overlay is open -> injection is suspended so nothing moves
        public static bool UiSuspendInjection;

        public static void ComputeInjectionFlags()
        {
            var c = Config;
            var w = Wheel;

            bool ready = c != null && c.Enabled.Value && c.UseWheelInput.Value
                         && w != null && w.Available && !w.Lost
                         && !UiSuspendInjection;

            if (!ready)
            {
                InjectSteer = InjectThrottle = InjectBrake = InjectClutch = InjectHandbrake = InjectNos = false;
                InputInjectionEnabled = false;
                return;
            }

            // Only inject channels whose axis physically exists. Pedals additionally only once
            // they have been seen at rest (guard against phantom full throttle/brake).
            string throttleSrc = c.ThrottleAxis.Value;
            string brakeSrc = c.CombinedPedals.Value ? c.ThrottleAxis.Value : c.BrakeAxis.Value;

            InjectSteer = w.HasAxis(c.SteerAxis.Value);
            InjectThrottle = w.HasAxis(throttleSrc) && w.ThrottleArmed;
            InjectBrake = w.HasAxis(brakeSrc) && w.BrakeArmed;
            InjectClutch = !string.IsNullOrEmpty(c.ClutchAxis.Value) && w.HasAxis(c.ClutchAxis.Value) && w.ClutchArmed;
            InjectHandbrake = w.HasHandbrakeInput(c.HandbrakeAxis.Value) || (c.HandbrakeButton.Value >= 0);
            InjectNos = c.NosButton.Value >= 0;

            InputInjectionEnabled = InjectSteer || InjectThrottle || InjectBrake || InjectClutch || InjectHandbrake || InjectNos;
        }
    }
}
