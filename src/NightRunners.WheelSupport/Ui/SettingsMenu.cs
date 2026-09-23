using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using NightRunners.WheelSupport.Config;
using NightRunners.WheelSupport.Ffb;
using NightRunners.WheelSupport.Input;

namespace NightRunners.WheelSupport.Ui
{
    // In-game settings menu (IMGUI). Axes, inverts, deadzones, FFB strengths (live), button
    // mapping via capture, and a "Recalibrate" button. Applies changes immediately by writing
    // into the ConfigEntry values; "Save" persists them to disk.
    public sealed class SettingsMenu
    {
        private enum Capture { None, Handbrake, Nos, ShiftUp, ShiftDown, ShifterG1, ShifterG2, ShifterG3, ShifterG4, ShifterG5, ShifterG6, ShifterGR, AxisSteer, AxisThrottle, AxisBrake, AxisClutch }

        private Capture _capture = Capture.None;
        private bool _btnArmed;
        private bool _vibTest;
        private float _joltStart = -999f;
        private float _joltUntil = -999f;
        private float _savedAt = -999f;
        private const float JoltDuration = 0.25f;
        private UnityEngine.Vector2 _scroll;
        private readonly Dictionary<int, float> _baseline = new Dictionary<int, float>();

        // Axis auto-detection ("detect" button next to each axis row): picks the axis whose value
        // moves the most while armed. All standard fields are candidates (universal).
        private Capture _axisCapture = Capture.None;
        private readonly Dictionary<int, float> _axisBase = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _axisMin = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _axisMax = new Dictionary<int, float>();

        public bool RecalibrateRequested { get; private set; }

        public void Begin(DirectInputWheel w)
        {
            _capture = Capture.None;
            _axisCapture = Capture.None;
            _vibTest = false;
            RecalibrateRequested = false;
            _baseline.Clear();
            _profileListDirty = true;
            if (w != null) foreach (var a in w.ReadAllAxes()) _baseline[a.Offset] = a.Norm;
        }

        // Per-frame while the menu is open. Handles button capture and drives FFB (quiet or test).
        public void Tick(DirectInputWheel w, float now)
        {
            if (w == null) return;

            if (_capture != Capture.None)
            {
                int p = w.FirstPressedButton();
                if (p < 0) _btnArmed = true;
                else if (_btnArmed)
                {
                    AssignCaptured(p);
                    _capture = Capture.None;
                }
            }

            // Axis detection: watch all fields, assign the one that moved the most.
            if (_axisCapture != Capture.None)
            {
                foreach (var a in w.ReadAllAxes())
                {
                    if (!_axisMin.TryGetValue(a.Offset, out var mn) || a.Norm < mn) _axisMin[a.Offset] = a.Norm;
                    if (!_axisMax.TryGetValue(a.Offset, out var mx) || a.Norm > mx) _axisMax[a.Offset] = a.Norm;
                }
                if (DetectMovedAxis(out var off2, out var extreme))
                {
                    AssignDetectedAxis(_axisCapture, off2, extreme);
                    _axisCapture = Capture.None;
                }
            }

            // While the menu is open, the menu owns the wheel FFB (ForceFeedbackController.Update
            // is not called): quiet unless testing. The test jolt is rendered here directly, since
            // routing it through the FFB controller would never reach the wheel while open.
            if (w.FfbAvailable)
            {
                float jolt = 0f;
                if (now < _joltUntil)
                {
                    float t = (now - _joltStart) / JoltDuration; // 0..1
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    // short, decaying left-right shake so it clearly feels like an impact
                    float dir = ((int)(t * 6f) % 2 == 0) ? 1f : -1f;
                    jolt = (1f - t) * dir;
                }
                w.SetConstantForce(jolt);
                w.SetVibration(_vibTest || now < _joltUntil ? 0.5f : 0f, now < _joltUntil ? 55f : 25f);
            }
        }

        private void AssignCaptured(int btn)
        {
            var cfg = ModContext.Config;
            if (cfg == null) return;
            switch (_capture)
            {
                case Capture.Handbrake: cfg.HandbrakeButton.Value = btn; break;
                case Capture.Nos: cfg.NosButton.Value = btn; break;
                case Capture.ShiftUp: cfg.ShiftUpButton.Value = btn; break;
                case Capture.ShiftDown: cfg.ShiftDownButton.Value = btn; break;
                case Capture.ShifterG1: cfg.ShifterGear1Button.Value = btn; break;
                case Capture.ShifterG2: cfg.ShifterGear2Button.Value = btn; break;
                case Capture.ShifterG3: cfg.ShifterGear3Button.Value = btn; break;
                case Capture.ShifterG4: cfg.ShifterGear4Button.Value = btn; break;
                case Capture.ShifterG5: cfg.ShifterGear5Button.Value = btn; break;
                case Capture.ShifterG6: cfg.ShifterGear6Button.Value = btn; break;
                case Capture.ShifterGR: cfg.ShifterReverseButton.Value = btn; break;
            }
            cfg.Save();
            ModLog.Info($"Assigned Button{btn} to {_capture} (saved).");
        }

        private void ArmAxisDetect(Capture cap, DirectInputWheel w)
        {
            _axisCapture = cap;
            _axisBase.Clear(); _axisMin.Clear(); _axisMax.Clear();
            if (w != null)
                foreach (var a in w.ReadAllAxes())
                {
                    _axisBase[a.Offset] = a.Norm;
                    _axisMin[a.Offset] = a.Norm;
                    _axisMax[a.Offset] = a.Norm;
                }
        }

        // The axis (of all standard fields) that moved the most since arming; needs a clear margin
        // over the runner-up so a bumped wheel cannot be mis-assigned.
        private bool DetectMovedAxis(out int offset, out float extremeNorm)
        {
            offset = -1; extremeNorm = 0f;
            float best = 0f, second = 0f; int bestOff = -1; float bestExt = 0f;
            foreach (var off in DirectInputWheel.StandardOffsets)
            {
                float baseN = _axisBase.TryGetValue(off, out var b) ? b : 0.5f;
                float mn = _axisMin.TryGetValue(off, out var a1) ? a1 : baseN;
                float mx = _axisMax.TryGetValue(off, out var a2) ? a2 : baseN;
                float devDown = Math.Abs(mn - baseN);
                float devUp = Math.Abs(mx - baseN);
                float dev = Math.Max(devDown, devUp);
                if (dev > best) { second = best; best = dev; bestOff = off; bestExt = devUp >= devDown ? mx : mn; }
                else if (dev > second) second = dev;
            }
            offset = bestOff; extremeNorm = bestExt;
            return best >= 0.25f && (best - second) >= 0.12f;
        }

        private void AssignDetectedAxis(Capture cap, int off, float extremeNorm)
        {
            var cfg = ModContext.Config;
            if (cfg == null) return;
            string name = DirectInputWheel.OffsetToConfigName(off);
            float baseN = _axisBase.TryGetValue(off, out var b) ? b : 0.5f;
            bool invert = extremeNorm < baseN; // pressed/full drives the raw value down
            switch (cap)
            {
                case Capture.AxisSteer: cfg.SteerAxis.Value = name; break; // steering invert stays manual
                case Capture.AxisThrottle: cfg.ThrottleAxis.Value = name; cfg.InvertThrottle.Value = invert; break;
                case Capture.AxisBrake: cfg.BrakeAxis.Value = name; cfg.InvertBrake.Value = invert; break;
                case Capture.AxisClutch: cfg.ClutchAxis.Value = name; cfg.InvertClutch.Value = invert; break;
            }
            cfg.Save();
            ModLog.Info($"Detected axis for {cap}: {name} (invert={invert}, saved).");
        }

        public void Draw(DirectInputWheel w, WheelConfig cfg)
        {
            // Height is capped to the screen so the panel never runs off the bottom; a scroll view
            // inside guarantees every row (down to Save / Shift down) is always reachable regardless
            // of the game's font size or resolution.
            float h = 900f;
            try { if (UnityEngine.Screen.height > 200 && h > UnityEngine.Screen.height - 60f) h = UnityEngine.Screen.height - 60f; } catch { }
            var rect = Gui.CenteredPanel(640f, h);
            UnityEngine.GUI.Box(rect, "NIGHT-RUNNERS Wheel Support - Settings (scroll for more)");
            // try/finally guarantees the area/scroll/group is closed even if a draw call throws.
            Gui.BeginArea(new UnityEngine.Rect(rect.x + 16f, rect.y + 32f, rect.width - 32f, rect.height - 44f));
            try
            {
                _scroll = Gui.BeginScroll(_scroll);
                try { DrawBody(w, cfg); }
                finally { Gui.EndScroll(); }
            }
            finally { Gui.EndArea(); }
        }

        private void DrawBody(DirectInputWheel w, WheelConfig cfg)
        {
            if (cfg == null) { Gui.Label("No config."); return; }

            Gui.Label(w != null && w.Available ? $"Device: {w.DeviceName}" : "No wheel connected.");
            Gui.Space(4f);
            Gui.Label("ALPHA NOTICE (v0.1.0-alpha): Early development build.");
            Gui.Label("Not all steering wheels or pedals are supported yet. Expect bugs, unusual car");
            Gui.Label("handling, or physics glitches. Tune steering linearity and FFB carefully.");
            Gui.Space(6f);

            Gui.Label("-- Axes (every field selectable; < > picks one, detect finds it by movement) --");
            AxisRow(w, cfg.SteerAxis, "Steer", Capture.AxisSteer);
            cfg.InvertSteer.Value = Gui.Toggle(cfg.InvertSteer.Value, "  invert steering");
            AxisRow(w, cfg.ThrottleAxis, "Throttle", Capture.AxisThrottle);
            cfg.InvertThrottle.Value = Gui.Toggle(cfg.InvertThrottle.Value, "  invert throttle");
            AxisRow(w, cfg.BrakeAxis, "Brake", Capture.AxisBrake);
            cfg.InvertBrake.Value = Gui.Toggle(cfg.InvertBrake.Value, "  invert brake");
            AxisRow(w, cfg.ClutchAxis, "Clutch", Capture.AxisClutch);
            cfg.InvertClutch.Value = Gui.Toggle(cfg.InvertClutch.Value, "  invert clutch");

            Gui.Space(6f);
            Gui.Label("-- Steering (how the CAR turns - independent of force feedback) --");
            SliderRow("Lock range", cfg.SteerRange, 0.2f, 1f);
            SliderRow("Linearity", cfg.SteerLinearity, 0.3f, 3f);
            SliderRow("Center deadzone", cfg.SteerDeadzone, 0f, 0.5f);
            SliderRow("Pedal deadzone", cfg.PedalDeadzone, 0f, 0.5f);
            Gui.Label("Lock range = steering strength: LOWER = stronger/more direct (less wheel");
            Gui.Label("rotation for full lock). Linearity >1 = calmer around center. These do not");
            Gui.Label("change the force feedback below.");

            Gui.Space(6f);
            Gui.Label("-- Force feedback --");
            cfg.FfbEnabled.Value = Gui.Toggle(cfg.FfbEnabled.Value, "FFB enabled");
            cfg.InvertFfb.Value = Gui.Toggle(cfg.InvertFfb.Value, "invert FFB direction");
            SliderRow("Master", cfg.FfbMasterGain, 0f, 1f);
            Gui.Label("  Centering feel (spring + damper):");
            SliderRow("  Centering", cfg.CenteringGain, 0f, 1f);
            SliderRow("  Center weight", cfg.CenterWeight, 0f, 0.6f);
            SliderRow("  Spring curve", cfg.SpringExponent, 0.5f, 3f);
            SliderRow("  Max force", cfg.MaxForce, 0.1f, 1f);
            SliderRow("  Damping", cfg.DampingGain, 0f, 1f);
            SliderRow("Speed vib", cfg.SpeedVibGain, 0f, 1f);
            SliderRow("RPM shake", cfg.RpmVibGain, 0f, 1f);
            SliderRow("Slip rumble", cfg.SlipRumbleGain, 0f, 1f);
            SliderRow("Crash", cfg.CrashGain, 0f, 1f);
            Gui.BeginH();
            if (Gui.Button("Test jolt")) { _joltStart = SafeNow(); _joltUntil = _joltStart + JoltDuration; }
            _vibTest = Gui.Toggle(_vibTest, "Vibration test");
            Gui.EndH();

            Gui.Space(6f);
            Gui.Label("-- Buttons (click Assign, then press the button on the wheel) --");
            ButtonRow(cfg.HandbrakeButton, "Handbrake", Capture.Handbrake);
            ButtonRow(cfg.ShiftUpButton, "Shift up", Capture.ShiftUp);
            ButtonRow(cfg.ShiftDownButton, "Shift down", Capture.ShiftDown);
            ButtonRow(cfg.NosButton, "NOS / boost", Capture.Nos);
            Gui.Label("Shift up/down operate the game's gearbox (sequential / paddle shifting).");

            Gui.Space(6f);
            Gui.Label("-- H-Shifter & Clutch --");
            cfg.KeyboardShifter.Value = Gui.Toggle(cfg.KeyboardShifter.Value, "Keyboard shifter emulation (Keys 1-6=Gears, R=Rev, 0/N=Neutral)");
            cfg.RequireClutchToShift.Value = Gui.Toggle(cfg.RequireClutchToShift.Value, "Require clutch pedal to shift (realistic manual)");
            string gName = ModContext.CurrentGearDisplay == -1 ? "Reverse" : (ModContext.CurrentGearDisplay == 0 ? "Neutral" : $"Gear {ModContext.CurrentGearDisplay}");
            float clPercent = ModContext.AxClutch * 100f;
            string clStatus = ModContext.AxClutch > 0.45f ? "DISENGAGED (Neutral)" : "ENGAGED";
            Gui.Label($"  Live Status: {gName} | Clutch: {clPercent:F0}% [{clStatus}]");
            Gui.Label("Physical H-Shifter buttons (optional; map each gear slot):");
            ButtonRow(cfg.ShifterGear1Button, "Gear 1", Capture.ShifterG1);
            ButtonRow(cfg.ShifterGear2Button, "Gear 2", Capture.ShifterG2);
            ButtonRow(cfg.ShifterGear3Button, "Gear 3", Capture.ShifterG3);
            ButtonRow(cfg.ShifterGear4Button, "Gear 4", Capture.ShifterG4);
            ButtonRow(cfg.ShifterGear5Button, "Gear 5", Capture.ShifterG5);
            ButtonRow(cfg.ShifterGear6Button, "Gear 6", Capture.ShifterG6);
            ButtonRow(cfg.ShifterReverseButton, "Reverse", Capture.ShifterGR);
            // Always draw this line (empty when idle) so the IMGUI control count is identical across
            // the Layout and event passes - a conditional label toggled by the Assign click desyncs
            // the layout and throws.
            Gui.Label(_capture != Capture.None ? $"  Waiting for a button for {_capture}... (release all first)" : "");

            DrawProfileSection(w, cfg);

            Gui.Space(10f);
            Gui.BeginH();
            if (Gui.Button("Save")) { cfg.Save(); _savedAt = SafeNow(); ModLog.Info("Settings saved."); }
            if (Gui.Button("Recalibrate axes")) RecalibrateRequested = true;
            if (Gui.Button("Close (F9 / ESC)")) _requestClose = true;
            Gui.EndH();
            // Unconditional label (empty when idle) keeps the IMGUI control count stable.
            Gui.Label(SafeNow() - _savedAt < 2.5f ? "Saved. Settings persist across restarts." : "Changes apply live and are saved automatically.");
        }

        private bool _requestClose;
        public bool ConsumeCloseRequest() { var c = _requestClose; _requestClose = false; return c; }

        // --- Profile management state (v0.1.0-alpha) ---
        private string _profileName = "";
        private List<string> _profileList = new List<string>();
        private bool _profileListDirty = true;
        private string _profileMessage = "";
        private float _profileMessageAt = -999f;
        private volatile string _pendingImportPath = null;
        private volatile string _pendingExportPath = null;
        private volatile string _pendingExportProfileName = null;
        private volatile bool _fileDialogOpen = false;

        // --- Config profiles as .json files (create / import / export / name / save / delete) ---
        private void DrawProfileSection(DirectInputWheel w, WheelConfig cfg)
        {
            Gui.Space(10f);
            Gui.Label("-- Config Profiles (.json: save, load, name, delete) --");

            // Consume any completed file dialogs from the background thread
            if (_pendingImportPath != null)
            {
                string path = _pendingImportPath;
                _pendingImportPath = null;
                if (ConfigProfileManager.ImportProfileFromPath(cfg.ConfigFile, path, true, out var msg))
                {
                    _profileMessage = msg;
                    _profileListDirty = true;
                }
                else
                {
                    _profileMessage = "Import failed: " + msg;
                }
                _profileMessageAt = SafeNow();
            }

            if (_pendingExportPath != null)
            {
                string path = _pendingExportPath;
                string profName = _pendingExportProfileName;
                _pendingExportPath = null;
                _pendingExportProfileName = null;
                if (ConfigProfileManager.ExportProfileToPath(cfg.ConfigFile, profName, path, out var msg))
                {
                    _profileMessage = msg;
                    _profileListDirty = true;
                }
                else
                {
                    _profileMessage = "Export failed: " + msg;
                }
                _profileMessageAt = SafeNow();
            }

            if (_profileListDirty)
            {
                _profileList = ConfigProfileManager.GetProfileNames();
                _profileListDirty = false;
            }

            Gui.BeginH();
            Gui.Label("Name:");
            _profileName = Gui.TextField(_profileName);
            if (Gui.Button("Save as") && !string.IsNullOrEmpty(_profileName))
            {
                if (ConfigProfileManager.ExportProfile(cfg.ConfigFile, _profileName, out var msg))
                {
                    _profileMessage = msg;
                    _profileMessageAt = SafeNow();
                    _profileListDirty = true;
                }
                else
                {
                    _profileMessage = "Export failed: " + msg;
                    _profileMessageAt = SafeNow();
                }
            }
            if (Gui.Button("Export to file...") && !_fileDialogOpen)
            {
                string defaultName = string.IsNullOrEmpty(_profileName) ? "custom_wheel_profile.json" : _profileName + ".json";
                _fileDialogOpen = true;
                _profileMessage = "Opening save dialog...";
                _profileMessageAt = SafeNow();
                NativeDialogs.SaveFileAsync("Export Current Profile As", defaultName, null, (chosenPath) =>
                {
                    _pendingExportProfileName = _profileName;
                    _pendingExportPath = chosenPath;
                    _fileDialogOpen = false;
                });
            }
            Gui.EndH();

            Gui.BeginH();
            if (Gui.Button("Import profile (choose file...)") && !_fileDialogOpen)
            {
                _fileDialogOpen = true;
                _profileMessage = "Opening file browser...";
                _profileMessageAt = SafeNow();
                NativeDialogs.OpenFileAsync("Select Wheel Profile to Import", null, (chosenPath) =>
                {
                    _pendingImportPath = chosenPath;
                    _fileDialogOpen = false;
                });
            }
            if (Gui.Button("Open profiles folder"))
            {
                ConfigProfileManager.OpenProfilesFolder();
            }
            Gui.EndH();

            Gui.Label($"Saved profiles ({_profileList.Count}):");

            if (_profileList.Count == 0)
            {
                Gui.Label("  (none yet - click Import profile or type a name and click Save as)");
            }
            else
            {
                // Draw every row first; execute the action after the loop. Mutating the list or
                // breaking mid-loop would unbalance BeginHorizontal/EndHorizontal between the
                // Layout and event passes and throw IMGUI desync errors.
                string loadName = null, exportItemName = null, deleteName = null;
                foreach (var name in _profileList)
                {
                    Gui.BeginH();
                    Gui.Label($"  {name}");
                    if (Gui.Button("Load")) loadName = name;
                    if (Gui.Button("Export")) exportItemName = name;
                    if (Gui.Button("Delete")) deleteName = name;
                    Gui.EndH();
                }

                if (loadName != null)
                {
                    if (ConfigProfileManager.ImportProfile(cfg.ConfigFile, loadName, out var msg))
                    {
                        _profileMessage = msg;
                        _profileMessageAt = SafeNow();
                    }
                    else
                    {
                        _profileMessage = "Load failed: " + msg;
                        _profileMessageAt = SafeNow();
                    }
                }
                if (exportItemName != null && !_fileDialogOpen)
                {
                    string profToExport = exportItemName;
                    _fileDialogOpen = true;
                    _profileMessage = "Opening save dialog...";
                    _profileMessageAt = SafeNow();
                    NativeDialogs.SaveFileAsync($"Export Profile '{profToExport}'", profToExport + ".json", null, (chosenPath) =>
                    {
                        _pendingExportProfileName = profToExport;
                        _pendingExportPath = chosenPath;
                        _fileDialogOpen = false;
                    });
                }
                if (deleteName != null)
                {
                    if (ConfigProfileManager.DeleteProfile(deleteName, out var msg))
                    {
                        _profileMessage = msg;
                        _profileMessageAt = SafeNow();
                        _profileListDirty = true;
                    }
                    else
                    {
                        _profileMessage = "Delete failed: " + msg;
                        _profileMessageAt = SafeNow();
                    }
                }
            }

            // Always drawn so the IMGUI control count stays stable between passes.
            string pm = SafeNow() - _profileMessageAt < 5f ? _profileMessage : (_fileDialogOpen ? "Waiting for file dialog in Windows..." : "");
            Gui.Label(string.IsNullOrEmpty(pm) ? " " : pm);
        }

        private void AxisRow(DirectInputWheel w, ConfigEntry<string> entry, string label, Capture cap)
        {
            Gui.BeginH();
            string val = string.IsNullOrEmpty(entry.Value) ? "(none)" : entry.Value;
            Gui.Label($"{label,-9}: {val,-10}");
            if (Gui.Button(" < ")) entry.Value = CycleAxis(w, entry.Value, -1);
            if (Gui.Button(" > ")) entry.Value = CycleAxis(w, entry.Value, +1);
            if (Gui.Button("clear")) entry.Value = "";
            if (Gui.Button("detect"))
            {
                if (_axisCapture == cap) _axisCapture = Capture.None; // click again = cancel
                else ArmAxisDetect(cap, w);
            }
            float norm = 0f, raw = 0f;
            if (w != null)
                foreach (var a in w.ReadAllAxes())
                    if (a.Config == entry.Value) { norm = a.Norm; raw = a.Raw; }
            string hint = _axisCapture == cap ? " (MOVE THE CONTROL NOW)" : "";
            Gui.Label($" {Gui.Bar(norm)} {raw:F0}{hint}");
            Gui.EndH();
        }

        private static string CycleAxis(DirectInputWheel w, string current, int dir)
        {
            var list = new List<string> { "" };
            list.AddRange(DirectInputWheel.PresentAxisConfigNames());
            int idx = list.IndexOf(current ?? "");
            if (idx < 0) idx = 0;
            idx = (idx + dir + list.Count) % list.Count;
            return list[idx];
        }

        private void SliderRow(string label, ConfigEntry<float> entry, float min, float max)
        {
            Gui.BeginH();
            Gui.Label($"{label,-16} {entry.Value,6:F2}");
            entry.Value = Gui.Slider(entry.Value, min, max);
            Gui.EndH();
        }

        private void ButtonRow(ConfigEntry<int> entry, string label, Capture cap)
        {
            Gui.BeginH();
            Gui.Label($"{label,-14}: {(entry.Value >= 0 ? "Button" + entry.Value : "(none)")}");
            if (Gui.Button("Assign")) { _capture = cap; _btnArmed = false; }
            if (Gui.Button("Clear")) entry.Value = -1;
            Gui.EndH();
        }

        private static float SafeNow()
        {
            try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; }
        }
    }
}
