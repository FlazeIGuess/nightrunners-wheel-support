using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NightRunners.WheelSupport.Config;
using SharpDX.DirectInput;

namespace NightRunners.WheelSupport.Input
{
    // A single live reading of one axis, used by the calibration wizard and settings menu.
    public struct AxisReading
    {
        public string Config;   // config name: X, Y, Z, RotationX, ...
        public string Display;  // DirectInput display name, e.g. "Accelerator"
        public int Offset;      // DirectInput data-format offset
        public int Raw;         // raw value
        public float Norm;      // 0..1 normalized to the axis range
    }

    // Wraps the wheel via SharpDX.DirectInput: reading axes/buttons + force feedback.
    // On Windows, Rewired reads through RawInput (non-exclusive), so our exclusive
    // DirectInput access usually does not block the game's own input.
    public sealed class DirectInputWheel : IDisposable
    {
        private const int DEFAULT_MIN = 0;
        private const int DEFAULT_MAX = 65535;

        // Universal axis set: every standard DirectInput field the mod can read and the user can
        // assign. Device enumeration is deliberately NOT used to gate this: "emulated" devices
        // (e.g. Logitech wheels in G HUB mode) enumerate axes at offsets that carry no data while
        // the real values stream in other standard fields.
        public static readonly int[] StandardOffsets = { 0, 4, 8, 12, 16, 20, 24, 28 };

        private DirectInput _di;
        private Joystick _joy;
        private JoystickState _state = new JoystickState();

        private Effect _constantEffect;   // centering force + crash jolt (ConstantForce)
        private Effect _periodicEffect;   // vibration + slip rumble (Sine)
        private EffectParameters _constParams;
        private EffectParameters _periodicParams;
        private ConstantForce _constForce;
        private PeriodicForce _periodicForce;
        private int _ffbAxisOffset = (int)JoystickOffset.X;

        // Present axes (offsets), their range, and their DirectInput display names.
        private readonly HashSet<int> _presentOffsets = new HashSet<int>();
        private readonly Dictionary<int, (int min, float span)> _axisRange = new Dictionary<int, (int, float)>();
        private readonly Dictionary<int, string> _axisDisplayName = new Dictionary<int, string>();

        private int _reacquireCounter;

        public bool Available { get; private set; }
        public bool Lost { get; private set; }          // device lost (alt-tab / unplugged)
        public bool FfbAvailable { get; private set; }
        public string DeviceName { get; private set; } = "(no device)";
        public int AxisCount { get; private set; }
        public int ButtonCount { get; private set; }

        // Normalized inputs (main thread, valid after Poll)
        public float Steer;      // -1..1 processed steering (deadzone + lock range + linearity) - for the car
        public float SteerRaw;   // -1..1 raw physical wheel position (invert only) - for the FFB spring
        public float Throttle;   // 0..1
        public float Brake;      // 0..1
        public float Clutch;     // 0..1
        public float Handbrake;  // 0..1

        // "Armed" = the pedal has been seen at rest (~0) at least once since init.
        // Guards against phantom full throttle when an axis is missing or wrongly inverted (idle = max).
        public bool ThrottleArmed { get; private set; }
        public bool BrakeArmed { get; private set; }
        public bool ClutchArmed { get; private set; }

        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        private static IntPtr ResolveWindowHandle()
        {
            try
            {
                var h = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (h != IntPtr.Zero) return h;
            }
            catch { }
            try { var h = GetActiveWindow(); if (h != IntPtr.Zero) return h; } catch { }
            try { return GetForegroundWindow(); } catch { }
            return IntPtr.Zero;
        }

        public bool TryInit(WheelConfig cfg)
        {
            try
            {
                _di = new DirectInput();

                var devices = _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.ForceFeedback | DeviceEnumerationFlags.AttachedOnly).ToList();
                if (devices.Count == 0)
                    devices = _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly).ToList();

                if (devices.Count == 0)
                {
                    ModLog.Debug("No wheel/controller found (yet). The game stays fully playable.");
                    SafeDispose();
                    return false;
                }

                ModLog.Info($"Input devices found: {devices.Count}");
                foreach (var d in devices)
                    ModLog.Info($"  - {d.InstanceName} [{d.Type}] (Product: {d.ProductName})");

                DeviceInstance chosen = null;
                var wanted = (cfg.DeviceName.Value ?? string.Empty).Trim();
                if (wanted.Length > 0)
                    chosen = devices.FirstOrDefault(d =>
                        (d.InstanceName ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (d.ProductName ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
                if (chosen == null) chosen = devices[0];

                _joy = new Joystick(_di, chosen.InstanceGuid);
                DeviceName = chosen.InstanceName;

                try { _joy.Properties.Range = new InputRange(DEFAULT_MIN, DEFAULT_MAX); }
                catch (Exception e) { ModLog.Debug($"Global range set not possible: {e.Message}"); }

                var caps = _joy.Capabilities;
                AxisCount = caps.AxeCount;
                ButtonCount = caps.ButtonCount;
                bool ffbFlag = (caps.Flags & DeviceFlags.ForceFeedback) != 0;
                ModLog.Info($"Selected: {DeviceName} | axes={AxisCount} buttons={ButtonCount} FFB={ffbFlag}");

                DetectAxes();

                var hwnd = ResolveWindowHandle();
                if (!TryAcquire(hwnd))
                {
                    ModLog.Warn("Could not acquire the wheel. The game stays fully playable.");
                    SafeDispose();
                    return false;
                }

                Available = true;
                Lost = false;
                ThrottleArmed = BrakeArmed = ClutchArmed = false;

                if (ffbFlag && cfg.FfbEnabled.Value)
                    SetupFfb();
                else if (!ffbFlag)
                    ModLog.Warn("Device reports NO force feedback. Axes work, FFB is disabled.");

                return true;
            }
            catch (Exception e)
            {
                ModLog.Error($"DirectInput init failed: {e}");
                SafeDispose();
                return false;
            }
        }

        // Detects present axes (offsets), their real range, and their display names.
        private void DetectAxes()
        {
            _presentOffsets.Clear();
            _axisRange.Clear();
            _axisDisplayName.Clear();
            try
            {
                var axes = _joy.GetObjects(DeviceObjectTypeFlags.Axis);
                var names = new List<string>();
                foreach (var o in axes)
                {
                    _presentOffsets.Add(o.Offset);
                    int min = DEFAULT_MIN, max = DEFAULT_MAX;
                    try
                    {
                        var range = _joy.GetObjectPropertiesById(o.ObjectId).Range;
                        min = range.Minimum; max = range.Maximum;
                    }
                    catch { }
                    float span = (max - min);
                    if (span < 1f) span = 1f;
                    _axisRange[o.Offset] = (min, span);
                    _axisDisplayName[o.Offset] = o.Name ?? OffsetToConfigName(o.Offset);
                    names.Add($"{o.Name}[{OffsetToConfigName(o.Offset)} off {o.Offset} {min}..{max}]");
                }
                ModLog.Info("Axes detected: " + string.Join(", ", names));
            }
            catch (Exception e) { ModLog.Debug($"Axis detection failed: {e.Message}"); }
        }

        private bool TryAcquire(IntPtr hwnd)
        {
            var attempts = new (CooperativeLevel level, string desc)[]
            {
                (CooperativeLevel.Exclusive | CooperativeLevel.Background, "Exclusive|Background"),
                (CooperativeLevel.Exclusive | CooperativeLevel.Foreground, "Exclusive|Foreground"),
                (CooperativeLevel.NonExclusive | CooperativeLevel.Background, "NonExclusive|Background (input only, no FFB)"),
            };
            foreach (var a in attempts)
            {
                try
                {
                    _joy.SetCooperativeLevel(hwnd, a.level);
                    _joy.Acquire();
                    ModLog.Info($"Wheel acquired: {a.desc}");
                    if ((a.level & CooperativeLevel.NonExclusive) != 0)
                        FfbAvailable = false;
                    return true;
                }
                catch (Exception e)
                {
                    ModLog.Debug($"Acquire {a.desc} failed: {e.Message}");
                    try { _joy.Unacquire(); } catch { }
                }
            }
            return false;
        }

        private void SetupFfb()
        {
            try
            {
                try { _joy.Properties.AutoCenter = false; } catch { }
                try { _joy.Properties.ForceFeedbackGain = 10000; } catch { }

                try
                {
                    var ffbAxes = _joy.GetObjects(DeviceObjectTypeFlags.ForceFeedbackActuator);
                    if (ffbAxes != null && ffbAxes.Count > 0)
                        _ffbAxisOffset = ffbAxes[0].Offset;
                }
                catch (Exception e) { ModLog.Debug($"FFB axis not determinable, using X: {e.Message}"); }

                _constForce = new ConstantForce { Magnitude = 0 };
                _constParams = new EffectParameters
                {
                    Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                    Duration = int.MaxValue,
                    SamplePeriod = 0,
                    Gain = 10000,
                    TriggerButton = -1,
                    TriggerRepeatInterval = int.MaxValue,
                    Axes = new[] { _ffbAxisOffset },
                    Directions = new[] { 0 },
                    Parameters = _constForce,
                };
                _constantEffect = new Effect(_joy, EffectGuid.ConstantForce, _constParams);
                _constantEffect.Start();

                _periodicForce = new PeriodicForce { Magnitude = 0, Offset = 0, Phase = 0, Period = 100000 };
                _periodicParams = new EffectParameters
                {
                    Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                    Duration = int.MaxValue,
                    SamplePeriod = 0,
                    Gain = 10000,
                    TriggerButton = -1,
                    TriggerRepeatInterval = int.MaxValue,
                    Axes = new[] { _ffbAxisOffset },
                    Directions = new[] { 0 },
                    Parameters = _periodicForce,
                };
                _periodicEffect = new Effect(_joy, EffectGuid.Sine, _periodicParams);
                _periodicEffect.Start();

                FfbAvailable = true;
                ModLog.Info($"Force feedback active (FFB axis offset {_ffbAxisOffset}). ConstantForce + Sine ready.");
            }
            catch (Exception e)
            {
                FfbAvailable = false;
                ModLog.Warn($"FFB setup failed (axes keep working): {e.Message}");
            }
        }

        // Reads the current device state. Returns false = poll failed (device lost).
        public bool Poll(WheelConfig cfg)
        {
            if (!Available || _joy == null) return false;
            try
            {
                _joy.Poll();
                _joy.GetCurrentState(ref _state);
                Lost = false;
            }
            catch (Exception e)
            {
                Lost = true;
                if ((_reacquireCounter++ % 30) == 0)
                {
                    ModLog.DebugThrottled("poll", 0f, 3f, $"Poll failed, reacquiring: {e.Message}");
                    try { _joy.Acquire(); } catch { }
                }
                return false;
            }
            _reacquireCounter = 0;

            Steer = MapSteer(cfg.SteerAxis.Value, cfg);
            SteerRaw = RawSteerPos(cfg.SteerAxis.Value);

            if (cfg.CombinedPedals.Value)
            {
                float combined = ReadAxis01(cfg.ThrottleAxis.Value, cfg.InvertThrottle.Value);
                Throttle = ApplyPedalCurve(Math.Max(0f, (combined - 0.5f) * 2f), cfg);
                Brake = ApplyPedalCurve(Math.Max(0f, (0.5f - combined) * 2f), cfg);
            }
            else
            {
                Throttle = ApplyPedalCurve(ReadAxis01(cfg.ThrottleAxis.Value, cfg.InvertThrottle.Value), cfg);
                Brake = ApplyPedalCurve(ReadAxis01(cfg.BrakeAxis.Value, cfg.InvertBrake.Value), cfg);
            }

            Clutch = string.IsNullOrEmpty(cfg.ClutchAxis.Value) ? 0f
                : ApplyPedalCurve(ReadAxis01(cfg.ClutchAxis.Value, cfg.InvertClutch.Value), cfg);

            Handbrake = ReadHandbrake(cfg.HandbrakeAxis.Value);

            if (Throttle < 0.2f) ThrottleArmed = true;
            if (Brake < 0.2f) BrakeArmed = true;
            if (Clutch < 0.2f) ClutchArmed = true;

            return true;
        }

        private float ReadHandbrake(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return 0f;
            if (spec.StartsWith("Button", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(spec.Substring(6), out var idx))
                    return GetButton(idx) ? 1f : 0f;
                return 0f;
            }
            return ReadAxis01(spec, false);
        }

        // --- Axis mapping via offsets ---

        public static int AxisNameToOffset(string axis)
        {
            if (string.IsNullOrEmpty(axis)) return -1;
            switch (axis.Trim().ToLowerInvariant())
            {
                case "x": return (int)JoystickOffset.X;                 // 0
                case "y": return (int)JoystickOffset.Y;                 // 4
                case "z": return (int)JoystickOffset.Z;                 // 8
                case "rotationx": case "rx": return (int)JoystickOffset.RotationX; // 12
                case "rotationy": case "ry": return (int)JoystickOffset.RotationY; // 16
                case "rotationz": case "rz": return (int)JoystickOffset.RotationZ; // 20
                case "slider0": case "slider": return 24;
                case "slider1": return 28;
                default: return -1;
            }
        }

        public static string OffsetToConfigName(int offset)
        {
            switch (offset)
            {
                case 0: return "X";
                case 4: return "Y";
                case 8: return "Z";
                case 12: return "RotationX";
                case 16: return "RotationY";
                case 20: return "RotationZ";
                case 24: return "Slider0";
                case 28: return "Slider1";
                default: return "?";
            }
        }

        private int ReadRawByOffset(int offset)
        {
            switch (offset)
            {
                case 0: return _state.X;
                case 4: return _state.Y;
                case 8: return _state.Z;
                case 12: return _state.RotationX;
                case 16: return _state.RotationY;
                case 20: return _state.RotationZ;
                case 24: return SliderAt(0);
                case 28: return SliderAt(1);
                default: return 0;
            }
        }

        private int SliderAt(int i)
        {
            var s = _state.Sliders;
            return (s != null && i < s.Length) ? s[i] : 0;
        }

        public bool HasAxis(string axis)
        {
            // Universal: every standard field is always readable, so selection is not gated by
            // the (possibly misleading) device enumeration. Unknown axis names stay rejected.
            return AxisNameToOffset(axis) >= 0;
        }

        public bool HasHandbrakeInput(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return false;
            if (spec.StartsWith("Button", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(spec.Substring(6), out var idx) && idx >= 0 && idx < ButtonCount;
            return HasAxis(spec);
        }

        private float NormByOffset(int offset)
        {
            int raw = ReadRawByOffset(offset);
            int min = DEFAULT_MIN; float span = DEFAULT_MAX - DEFAULT_MIN;
            if (_axisRange.TryGetValue(offset, out var r)) { min = r.min; span = r.span; }
            float n = (raw - min) / span;
            return n < 0f ? 0f : (n > 1f ? 1f : n);
        }

        // Live readings of all present axes (for wizard / menu).
        public List<AxisReading> ReadAllAxes()
        {
            var list = new List<AxisReading>(StandardOffsets.Length);
            foreach (var off in StandardOffsets)
            {
                list.Add(new AxisReading
                {
                    Config = OffsetToConfigName(off),
                    Display = _axisDisplayName.TryGetValue(off, out var dn) ? dn : OffsetToConfigName(off),
                    Offset = off,
                    Raw = ReadRawByOffset(off),
                    Norm = NormByOffset(off),
                });
            }
            return list;
        }

        public static List<string> PresentAxisConfigNames()
        {
            // Universal: all standard fields are always selectable.
            return StandardOffsets.Select(OffsetToConfigName).ToList();
        }

        public string AxisDisplayName(string configName)
        {
            int off = AxisNameToOffset(configName);
            return (off >= 0 && _axisDisplayName.TryGetValue(off, out var dn)) ? dn : configName;
        }

        public bool GetButton(int i)
        {
            var b = _state.Buttons;
            return b != null && i >= 0 && i < b.Length && b[i];
        }

        public int FirstPressedButton()
        {
            var b = _state.Buttons;
            if (b == null) return -1;
            for (int i = 0; i < b.Length; i++)
                if (b[i]) return i;
            return -1;
        }

        // roughly 0..1 for a named axis, honoring the real range, then optionally inverted
        private float ReadAxis01(string axis, bool invert)
        {
            int off = AxisNameToOffset(axis);
            if (off < 0) return 0f;
            float n = NormByOffset(off);
            return invert ? 1f - n : n;
        }

        private float ApplyPedalCurve(float v, WheelConfig cfg)
        {
            float dz = cfg.PedalDeadzone.Value;
            if (v <= dz) return 0f;
            return (v - dz) / (1f - dz);
        }

        // Raw physical wheel position -1..1 (device center = 0), WITHOUT deadzone / lock range /
        // linearity. This drives the FFB spring so the force reflects the real wheel angle and is
        // decoupled from the input mapping (which shapes what the car receives, not what you feel).
        // Note: InvertSteer is deliberately NOT applied here - it remaps the car direction, while the
        // physical centering must always pull toward the physical center. Device force direction is
        // handled by the InvertFfb option.
        private float RawSteerPos(string axis)
        {
            int off = AxisNameToOffset(axis);
            if (off < 0) return 0f;
            float n = NormByOffset(off);      // 0..1
            float s = n * 2f - 1f;            // -1..1
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            return s;
        }

        private float MapSteer(string axis, WheelConfig cfg)
        {
            int off = AxisNameToOffset(axis);
            if (off < 0) return 0f;
            float n = NormByOffset(off);       // 0..1
            float s = n * 2f - 1f;             // -1..1
            if (cfg.InvertSteer.Value) s = -s;

            float range = cfg.SteerRange.Value;
            if (range > 0.001f) s /= range;
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;

            float dz = cfg.SteerDeadzone.Value;
            float mag = Math.Abs(s);
            if (mag <= dz) return 0f;
            mag = (mag - dz) / (1f - dz);

            float g = cfg.SteerLinearity.Value;
            if (Math.Abs(g - 1f) > 0.01f) mag = (float)Math.Pow(mag, g);

            return s < 0 ? -mag : mag;
        }

        // --- FFB output ---

        public void SetConstantForce(float signed)
        {
            if (!FfbAvailable || _constantEffect == null) return;
            if (signed > 1f) signed = 1f; else if (signed < -1f) signed = -1f;
            int mag = (int)(signed * 10000f);
            try
            {
                _constForce.Magnitude = mag;
                _constParams.Parameters = _constForce;
                // Only update the magnitude, do NOT restart (would cause per-frame stutter).
                // The running effect is refreshed every 20 s via RearmEffects().
                _constantEffect.SetParameters(_constParams, EffectParameterFlags.TypeSpecificParameters);
            }
            catch (Exception e) { ModLog.DebugThrottled("setconst", 0f, 3f, $"SetConstantForce error: {e.Message}"); }
        }

        public void SetVibration(float amount, float hz)
        {
            if (!FfbAvailable || _periodicEffect == null) return;
            if (amount < 0f) amount = 0f; else if (amount > 1f) amount = 1f;
            int mag = (int)(amount * 10000f);
            if (hz < 1f) hz = 1f;
            int periodMicros = (int)(1000000f / hz);
            if (periodMicros < 1000) periodMicros = 1000; // never let the period reach ~0 (min 1 ms)
            try
            {
                _periodicForce.Magnitude = mag;
                _periodicForce.Period = periodMicros;
                _periodicParams.Parameters = _periodicForce;
                _periodicEffect.SetParameters(_periodicParams, EffectParameterFlags.TypeSpecificParameters);
            }
            catch (Exception e) { ModLog.DebugThrottled("setvib", 0f, 3f, $"SetVibration error: {e.Message}"); }
        }

        public void RearmEffects()
        {
            try { _constantEffect?.Start(); } catch { }
            try { _periodicEffect?.Start(); } catch { }
        }

        public void StopAllFfb()
        {
            try { _constantEffect?.Stop(); } catch { }
            try { _periodicEffect?.Stop(); } catch { }
        }

        public string RawAxesDebug()
        {
            return $"X={_state.X} Y={_state.Y} Z={_state.Z} RX={_state.RotationX} RY={_state.RotationY} RZ={_state.RotationZ} " +
                   $"S0={SliderAt(0)} S1={SliderAt(1)}";
        }

        public void Dispose() => SafeDispose();

        private void SafeDispose()
        {
            try { StopAllFfb(); } catch { }
            try { _constantEffect?.Dispose(); } catch { }
            try { _periodicEffect?.Dispose(); } catch { }
            try { _joy?.Unacquire(); } catch { }
            try { _joy?.Dispose(); } catch { }
            try { _di?.Dispose(); } catch { }
            _constantEffect = null; _periodicEffect = null; _joy = null; _di = null;
            Available = false; FfbAvailable = false;
        }
    }
}
