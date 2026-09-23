using BepInEx.Configuration;

namespace NightRunners.WheelSupport.Config
{
    // All tunable values of the mod. After the first start they live in
    // BepInEx/config/com.flaze.nightrunners.wheelsupport.cfg and can be edited live
    // (changes take effect the next time the value is used). The in-game calibration
    // wizard and settings menu write directly into these entries and call Save().
    public sealed class WheelConfig
    {
        // --- General ---
        public ConfigEntry<bool> Enabled;               // master on/off
        public ConfigEntry<bool> UseWheelInput;         // true = wheel drives; false = original input (FFB only / off)
        public ConfigEntry<string> DeviceName;          // substring of the device name; empty = first FFB device
        public ConfigEntry<bool> VerboseLogging;        // detailed axis / FFB logging for tuning
        public ConfigEntry<bool> Calibrated;            // set true once the wizard has been completed and saved
        public ConfigEntry<string> CalibrateHotkey;     // key that (re)opens the calibration wizard
        public ConfigEntry<string> MenuHotkey;          // key that opens the settings menu
        public ConfigEntry<bool> ShowWatermark;         // show top-right on-screen watermark HUD (F8 toggles)

        // --- Axis assignment (DirectInput axis name: X, Y, Z, RotationX, RotationY, RotationZ, Slider0, Slider1) ---
        public ConfigEntry<string> SteerAxis;
        public ConfigEntry<string> ThrottleAxis;
        public ConfigEntry<string> BrakeAxis;
        public ConfigEntry<string> ClutchAxis;
        public ConfigEntry<string> HandbrakeAxis;       // "" = off; axis name, or a button e.g. "Button5"

        // --- Axis calibration ---
        public ConfigEntry<bool> InvertSteer;
        public ConfigEntry<bool> InvertThrottle;
        public ConfigEntry<bool> InvertBrake;
        public ConfigEntry<bool> InvertClutch;
        public ConfigEntry<bool> CombinedPedals;        // throttle+brake on ONE split axis (throttle upper half, brake lower half)
        public ConfigEntry<float> SteerDeadzone;        // 0..0.5 center deadzone
        public ConfigEntry<float> PedalDeadzone;

        // --- Steering feel ---
        public ConfigEntry<float> SteerLinearity;       // gamma curve; 1 = linear, >1 = finer around center
        public ConfigEntry<float> SteerRange;           // fraction of wheel travel used for full lock (0.2..1)

        // --- Force feedback: master ---
        public ConfigEntry<bool> FfbEnabled;
        public ConfigEntry<float> FfbMasterGain;        // 0..1 overall strength
        public ConfigEntry<bool> InvertFfb;             // flip force direction (if the wheel pulls away instead of centering)

        // --- FFB: centering spring + damper (constant force, from the raw wheel position) ---
        public ConfigEntry<float> CenteringGain;        // 0..1 overall spring strength
        public ConfigEntry<float> CenteringMinSpeed;    // speed at which centering starts (in game speed units)
        public ConfigEntry<float> CenteringSpeedRef;    // speed at which centering reaches its maximum
        public ConfigEntry<float> CenterWeight;         // 0..1 immediate off-center resistance (pretension, no dead zone)
        public ConfigEntry<float> SpringExponent;       // 0.5..3 spring curve: 1 = linear, >1 = progressive
        public ConfigEntry<float> MaxForce;             // 0..1 cap on the centering force (prevents lock-up)
        public ConfigEntry<float> DampingGain;          // 0..1 resistance against fast wheel movement (enables counter-steer)

        // --- FFB: speed / RPM vibration (sine periodic) ---
        public ConfigEntry<float> SpeedVibGain;         // 0..1
        public ConfigEntry<float> SpeedVibMinHz;
        public ConfigEntry<float> SpeedVibMaxHz;
        public ConfigEntry<float> RpmVibGain;           // extra shake near redline

        // --- FFB: slip / drift rumble ---
        public ConfigEntry<float> SlipRumbleGain;       // 0..1
        public ConfigEntry<float> SlipThreshold;        // slip value at which the rumble kicks in

        // --- FFB: crash impulse ---
        public ConfigEntry<float> CrashGain;            // 0..1
        public ConfigEntry<float> CrashMinImpulse;      // minimum collision strength that produces a jolt
        public ConfigEntry<float> CrashDurationMs;      // duration of the jolt effect
        public ConfigEntry<float> CrashCooldownMs;      // minimum time between two jolts

        // --- Button mapping (M3). -1 = unmapped. Buttons are DirectInput button indices. ---
        public ConfigEntry<int> HandbrakeButton;
        public ConfigEntry<int> NosButton;
        public ConfigEntry<int> ShiftUpButton;
        public ConfigEntry<int> ShiftDownButton;

        // --- Shifter & Transmission ---
        public ConfigEntry<bool> KeyboardShifter;
        public ConfigEntry<bool> RequireClutchToShift;
        public ConfigEntry<int> ShifterGear1Button;
        public ConfigEntry<int> ShifterGear2Button;
        public ConfigEntry<int> ShifterGear3Button;
        public ConfigEntry<int> ShifterGear4Button;
        public ConfigEntry<int> ShifterGear5Button;
        public ConfigEntry<int> ShifterGear6Button;
        public ConfigEntry<int> ShifterReverseButton;

        private ConfigFile _file;

        public void Bind(ConfigFile cfg)
        {
            _file = cfg;
            const string G = "1. General";
            Enabled = cfg.Bind(G, "Enabled", true, "Master switch for the mod.");
            UseWheelInput = cfg.Bind(G, "UseWheelInput", true,
                "true: the wheel drives the car. false: keep the original controls, only force feedback runs (or nothing).");
            DeviceName = cfg.Bind(G, "DeviceName", "",
                "Part of the wheel's name (e.g. 'G29', 'Thrustmaster'). Empty = first force-feedback device found.");
            VerboseLogging = cfg.Bind(G, "VerboseLogging", false,
                "Detailed logging (raw axis values, FFB forces). Turn on to calibrate, then turn off again.");
            Calibrated = cfg.Bind(G, "Calibrated", false,
                "Set to true automatically once the calibration wizard has been completed. Set to false to run the wizard again on the next start.");
            CalibrateHotkey = cfg.Bind(G, "CalibrateHotkey", "F10",
                "Key that opens the calibration wizard at any time (UnityEngine.KeyCode name, e.g. F10).");
            MenuHotkey = cfg.Bind(G, "MenuHotkey", "F9",
                "Key that opens the settings menu (UnityEngine.KeyCode name, e.g. F9).");
            ShowWatermark = cfg.Bind(G, "ShowWatermark", true,
                "Show top-right on-screen watermark HUD with mod version and hotkeys. Press F8 to toggle.");

            const string A = "2. Axes";
            SteerAxis = cfg.Bind(A, "SteerAxis", "X", "DirectInput axis for steering: X, Y, Z, RotationX, RotationY, RotationZ, Slider0, Slider1. All fields are always selectable; use the wizard or the in-game menu to (re)assign.");
            ThrottleAxis = cfg.Bind(A, "ThrottleAxis", "Y", "Axis for the throttle.");
            BrakeAxis = cfg.Bind(A, "BrakeAxis", "RotationZ", "Axis for the brake.");
            ClutchAxis = cfg.Bind(A, "ClutchAxis", "Slider0", "Axis for the clutch (optional, '' = off).");
            HandbrakeAxis = cfg.Bind(A, "HandbrakeAxis", "", "Axis or button for the handbrake (optional). Button e.g. 'Button4'.");

            const string C = "3. Calibration";
            InvertSteer = cfg.Bind(C, "InvertSteer", false, "Reverse the steering direction.");
            InvertThrottle = cfg.Bind(C, "InvertThrottle", true, "Invert the throttle pedal (many pedals report released = maximum value).");
            InvertBrake = cfg.Bind(C, "InvertBrake", true, "Invert the brake pedal.");
            InvertClutch = cfg.Bind(C, "InvertClutch", true, "Invert the clutch.");
            CombinedPedals = cfg.Bind(C, "CombinedPedals", false,
                "Throttle and brake on ONE split axis (throttle upper half, brake lower half). For some wheels without separate pedal drivers.");
            SteerDeadzone = cfg.Bind(C, "SteerDeadzone", 0.0f, new ConfigDescription("Deadzone around the steering center.", new AcceptableValueRange<float>(0f, 0.5f)));
            PedalDeadzone = cfg.Bind(C, "PedalDeadzone", 0.03f, new ConfigDescription("Deadzone at the start of pedal travel.", new AcceptableValueRange<float>(0f, 0.5f)));

            const string F = "4. Steering Feel";
            SteerLinearity = cfg.Bind(F, "SteerLinearity", 1.0f, new ConfigDescription("Steering curve (gamma). 1 = linear, >1 = finer around center.", new AcceptableValueRange<float>(0.3f, 3f)));
            SteerRange = cfg.Bind(F, "SteerRange", 0.5f, new ConfigDescription("Fraction of wheel travel used for full lock. Smaller = less rotation needed for full steering (0.5 = about 225 degrees on a 900 degree wheel). Live-tunable in the F9 menu.", new AcceptableValueRange<float>(0.2f, 1f)));

            const string M = "5. FFB Master";
            FfbEnabled = cfg.Bind(M, "FfbEnabled", true, "Enable force feedback.");
            FfbMasterGain = cfg.Bind(M, "FfbMasterGain", 0.8f, new ConfigDescription("Overall strength of all FFB effects.", new AcceptableValueRange<float>(0f, 1f)));
            InvertFfb = cfg.Bind(M, "InvertFfb", false, "Flip the force direction. If the wheel pulls AWAY from center instead of toward it: toggle this.");

            const string CE = "6. FFB Centering";
            CenteringGain = cfg.Bind(CE, "CenteringGain", 0.7f, new ConfigDescription("Overall strength of the self-centering spring.", new AcceptableValueRange<float>(0f, 1f)));
            CenteringMinSpeed = cfg.Bind(CE, "CenteringMinSpeed", 1.0f, "Speed value at which centering begins (in game speed units).");
            CenteringSpeedRef = cfg.Bind(CE, "CenteringSpeedRef", 25.0f, "Speed value at which centering reaches its maximum. Lower = the force builds up sooner.");
            CenterWeight = cfg.Bind(CE, "CenterWeight", 0.12f, new ConfigDescription("Immediate off-center resistance (pretension). Gives a light, smooth feel right off center - no dead zone. Higher = more weight around center.", new AcceptableValueRange<float>(0f, 0.6f)));
            SpringExponent = cfg.Bind(CE, "SpringExponent", 1.3f, new ConfigDescription("Spring curve. 1 = linear (force grows evenly with angle), >1 = progressive (softer near center, firmer at the rim), <1 = more force early.", new AcceptableValueRange<float>(0.5f, 3f)));
            MaxForce = cfg.Bind(CE, "MaxForce", 0.85f, new ConfigDescription("Upper cap on the centering force so the wheel never locks up and stays steerable with one hand.", new AcceptableValueRange<float>(0.1f, 1f)));
            DampingGain = cfg.Bind(CE, "DampingGain", 0.15f, new ConfigDescription("Resistance against fast wheel movement. Damps the snappy feel and makes counter-steering at speed possible. 0 = off.", new AcceptableValueRange<float>(0f, 1f)));

            const string SV = "7. FFB Vibration";
            SpeedVibGain = cfg.Bind(SV, "SpeedVibGain", 0.15f, new ConfigDescription("Strength of the speed vibration.", new AcceptableValueRange<float>(0f, 1f)));
            SpeedVibMinHz = cfg.Bind(SV, "SpeedVibMinHz", 8.0f, new ConfigDescription("Vibration frequency at low speed/RPM.", new AcceptableValueRange<float>(1f, 200f)));
            SpeedVibMaxHz = cfg.Bind(SV, "SpeedVibMaxHz", 45.0f, new ConfigDescription("Vibration frequency at high speed/RPM.", new AcceptableValueRange<float>(1f, 200f)));
            RpmVibGain = cfg.Bind(SV, "RpmVibGain", 0.2f, new ConfigDescription("Extra shake near the rev limiter (redline).", new AcceptableValueRange<float>(0f, 1f)));

            const string SR = "8. FFB Slip Rumble";
            SlipRumbleGain = cfg.Bind(SR, "SlipRumbleGain", 0.35f, new ConfigDescription("Strength of the rumble during wheel slip / drift.", new AcceptableValueRange<float>(0f, 1f)));
            SlipThreshold = cfg.Bind(SR, "SlipThreshold", 0.2f, "Slip value at which the rumble kicks in. Calibrate from the log during the first test.");

            const string CR = "9. FFB Crash";
            CrashGain = cfg.Bind(CR, "CrashGain", 0.9f, new ConfigDescription("Strength of the crash jolt.", new AcceptableValueRange<float>(0f, 1f)));
            CrashMinImpulse = cfg.Bind(CR, "CrashMinImpulse", 3.0f, "Minimum collision strength (relativeVelocity) for a jolt.");
            CrashDurationMs = cfg.Bind(CR, "CrashDurationMs", 220f, "Duration of the crash jolt in milliseconds.");
            CrashCooldownMs = cfg.Bind(CR, "CrashCooldownMs", 350f, "Minimum time between two crash jolts in milliseconds.");

            const string B = "10. Buttons";
            HandbrakeButton = cfg.Bind(B, "HandbrakeButton", -1, "DirectInput button index for the handbrake (-1 = unmapped). Set via the settings menu.");
            NosButton = cfg.Bind(B, "NosButton", -1, "DirectInput button index for NOS / boost (-1 = unmapped).");
            ShiftUpButton = cfg.Bind(B, "ShiftUpButton", -1, "DirectInput button index for shift up (-1 = unmapped).");
            ShiftDownButton = cfg.Bind(B, "ShiftDownButton", -1, "DirectInput button index for shift down (-1 = unmapped).");

            const string SH = "11. Shifter & Transmission";
            KeyboardShifter = cfg.Bind(SH, "KeyboardShifter", true, "Enable keyboard emulation for H-shifter (Keys 1-6 = Gears 1-6, R = Reverse, 0/N = Neutral). Lets you test manual shifting without hardware.");
            RequireClutchToShift = cfg.Bind(SH, "RequireClutchToShift", false, "When true, shifting into a gear requires pressing the clutch pedal (realistic manual). When false, shifting engages directly.");
            ShifterGear1Button = cfg.Bind(SH, "ShifterGear1Button", -1, "DirectInput button index for 1st gear on physical H-shifter (-1 = unmapped).");
            ShifterGear2Button = cfg.Bind(SH, "ShifterGear2Button", -1, "DirectInput button index for 2nd gear on physical H-shifter (-1 = unmapped).");
            ShifterGear3Button = cfg.Bind(SH, "ShifterGear3Button", -1, "DirectInput button index for 3rd gear on physical H-shifter (-1 = unmapped).");
            ShifterGear4Button = cfg.Bind(SH, "ShifterGear4Button", -1, "DirectInput button index for 4th gear on physical H-shifter (-1 = unmapped).");
            ShifterGear5Button = cfg.Bind(SH, "ShifterGear5Button", -1, "DirectInput button index for 5th gear on physical H-shifter (-1 = unmapped).");
            ShifterGear6Button = cfg.Bind(SH, "ShifterGear6Button", -1, "DirectInput button index for 6th gear on physical H-shifter (-1 = unmapped).");
            ShifterReverseButton = cfg.Bind(SH, "ShifterReverseButton", -1, "DirectInput button index for Reverse on physical H-shifter (-1 = unmapped).");
        }

        public ConfigFile ConfigFile => _file;

        public void Save()
        {
            try { _file?.Save(); } catch { }
        }

        // Toggle BepInEx' auto-save-on-set. Turned off while the settings menu is open so that
        // dragging a slider (which sets .Value every frame) does not trigger a synchronous
        // whole-file write per frame; an explicit Save() on close persists the result.
        public void SetAutoSave(bool on)
        {
            try { if (_file != null) _file.SaveOnConfigSet = on; } catch { }
        }
    }
}
