using System;
using System.Collections.Generic;
using System.Linq;
using NightRunners.WheelSupport.Config;
using NightRunners.WheelSupport.Input;

namespace NightRunners.WheelSupport.Ui
{
    public enum WizardResult { None, Saved, Cancelled }

    // Step-by-step calibration wizard (IMGUI). Auto-detects the moving axis and its direction,
    // pre-suggests by DirectInput axis name, and captures buttons for handbrake, paddle shifters,
    // and physical H-shifter gears. Also allows instant profile import or loading from the intro screen.
    public sealed class CalibrationWizard
    {
        private enum Step { Intro, SteerLeft, SteerRight, Throttle, Brake, Clutch, Handbrake, ShiftUp, ShiftDown, Shifter, Summary }

        private const float MoveThreshold = 0.25f; // normalized deviation that counts as "moved"

        private Step _step;
        private readonly List<int> _offsets = new List<int>();
        private readonly Dictionary<int, float> _baseline = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _minObs = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _maxObs = new Dictionary<int, float>();

        // Device-wake detection
        private readonly Dictionary<int, float> _lastNorm = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _jumpTime = new Dictionary<int, float>();
        private string _warn = "";

        // Fast-track profile import state
        private volatile string _pendingImportPath = null;
        private volatile bool _fileDialogOpen = false;

        // Detected results
        private int _steerOffset = -1;
        private int _throttleOffset = -1;
        private int _brakeOffset = -1;
        private int _clutchOffset = -1;
        private float _steerLeftNorm, _steerRightNorm;
        private string _steerAxis; private bool _invSteer;
        private string _throttleAxis; private bool _invThrottle;
        private string _brakeAxis; private bool _invBrake;
        private string _clutchAxis = ""; private bool _invClutch; private bool _clutchSet;
        private int _handbrakeButton = -1;
        private int _shiftUpButton = -1;
        private int _shiftDownButton = -1;

        // Physical H-Shifter bindings (-1 = unmapped)
        private int _shifterG1 = -1;
        private int _shifterG2 = -1;
        private int _shifterG3 = -1;
        private int _shifterG4 = -1;
        private int _shifterG5 = -1;
        private int _shifterG6 = -1;
        private int _shifterGR = -1;

        // Button capture (shared across handbrake / shift-up / shift-down)
        private bool _btnArmed;
        private int _capturedButton = -1;

        public void Begin(DirectInputWheel w, WheelConfig cfg = null)
        {
            _step = Step.Intro;
            _warn = "";
            _pendingImportPath = null;
            _fileDialogOpen = false;
            _steerOffset = _throttleOffset = _brakeOffset = _clutchOffset = -1;
            _steerAxis = null; _throttleAxis = null; _brakeAxis = null;
            _clutchAxis = ""; _clutchSet = false;
            _handbrakeButton = cfg?.HandbrakeButton?.Value ?? -1;
            _shiftUpButton = cfg?.ShiftUpButton?.Value ?? -1;
            _shiftDownButton = cfg?.ShiftDownButton?.Value ?? -1;
            _shifterG1 = cfg?.ShifterGear1Button?.Value ?? -1;
            _shifterG2 = cfg?.ShifterGear2Button?.Value ?? -1;
            _shifterG3 = cfg?.ShifterGear3Button?.Value ?? -1;
            _shifterG4 = cfg?.ShifterGear4Button?.Value ?? -1;
            _shifterG5 = cfg?.ShifterGear5Button?.Value ?? -1;
            _shifterG6 = cfg?.ShifterGear6Button?.Value ?? -1;
            _shifterGR = cfg?.ShifterReverseButton?.Value ?? -1;
            _offsets.Clear(); _baseline.Clear();
            _lastNorm.Clear(); _jumpTime.Clear();
            RefreshOffsets(w);
            StartTracking(w);
        }

        private void StartTracking(DirectInputWheel w)
        {
            _minObs.Clear(); _maxObs.Clear();
            if (w == null) return;
            foreach (var a in w.ReadAllAxes())
            {
                _minObs[a.Offset] = a.Norm;
                _maxObs[a.Offset] = a.Norm;
            }
        }

        private void RefreshOffsets(DirectInputWheel w)
        {
            _offsets.Clear();
            foreach (var off in DirectInputWheel.StandardOffsets) _offsets.Add(off);
        }

        private void CaptureBaseline(DirectInputWheel w)
        {
            _baseline.Clear();
            if (w == null) return;
            RefreshOffsets(w);
            foreach (var a in w.ReadAllAxes()) _baseline[a.Offset] = a.Norm;
        }

        // Called every frame while the wizard is open (main thread).
        public void Tick(DirectInputWheel w)
        {
            if (w == null) return;
            float now = NowSafe();
            var axes = w.ReadAllAxes();

            // 1) device-wake detection
            bool jumpSeen = false;
            foreach (var a in axes)
            {
                if (_lastNorm.TryGetValue(a.Offset, out var prev) && Math.Abs(a.Norm - prev) >= 0.22f)
                {
                    _jumpTime[a.Offset] = now;
                    jumpSeen = true;
                }
                _lastNorm[a.Offset] = a.Norm;
            }
            if (jumpSeen)
            {
                int recent = 0;
                foreach (var kv in _jumpTime) if (now - kv.Value <= 0.3f) recent++;
                if (recent >= 2)
                {
                    foreach (var kv in _jumpTime)
                    {
                        if (now - kv.Value > 0.3f) continue;
                        int off = kv.Key;
                        float v = NormOf(axes, off);
                        _baseline[off] = v;
                        _minObs[off] = v;
                        _maxObs[off] = v;
                    }
                    _jumpTime.Clear();
                    ModLog.Info($"Device wake detected: {recent} axes jumped together - baseline refreshed.");
                    _warn = "Device just woke up - readings reset. Repeat the step and click Detect again.";
                }
            }

            // 2) regular min/max tracking for the current step
            foreach (var a in axes)
            {
                if (!_minObs.TryGetValue(a.Offset, out var mn) || a.Norm < mn) _minObs[a.Offset] = a.Norm;
                if (!_maxObs.TryGetValue(a.Offset, out var mx) || a.Norm > mx) _maxObs[a.Offset] = a.Norm;
            }

            if (_step == Step.Handbrake || _step == Step.ShiftUp || _step == Step.ShiftDown)
            {
                int p = w.FirstPressedButton();
                if (p < 0) _btnArmed = true;
                else if (_btnArmed && _capturedButton < 0) _capturedButton = p;
            }
        }

        private void EnterButtonStep(Step s) { _btnArmed = false; _capturedButton = -1; _warn = ""; _step = s; }

        private bool DetectMovedAxis(HashSet<int> exclude, out int offset, out float extremeNorm)
        {
            return DetectMovedAxis(exclude, out offset, out extremeNorm, out _, out _);
        }

        private bool DetectMovedAxis(HashSet<int> exclude, out int offset, out float extremeNorm,
            out float bestDev, out float secondDev)
        {
            offset = -1; extremeNorm = 0f;
            float best = 0f, second = 0f;
            foreach (var off in _offsets)
            {
                if (exclude != null && exclude.Contains(off)) continue;
                float baseN = _baseline.TryGetValue(off, out var b) ? b : 0.5f;
                float mn = _minObs.TryGetValue(off, out var a1) ? a1 : baseN;
                float mx = _maxObs.TryGetValue(off, out var a2) ? a2 : baseN;
                float devDown = Math.Abs(mn - baseN);
                float devUp = Math.Abs(mx - baseN);
                float dev = Math.Max(devDown, devUp);
                if (dev > best)
                {
                    second = best;
                    best = dev;
                    offset = off;
                    extremeNorm = devUp >= devDown ? mx : mn;
                }
                else if (dev > second) second = dev;
            }
            bestDev = best; secondDev = second;
            return best >= MoveThreshold && (best - second) >= 0.12f;
        }

        private static float NormOf(List<AxisReading> axes, int offset)
        {
            foreach (var a in axes) if (a.Offset == offset) return a.Norm;
            return 0.5f;
        }

        private static float NowSafe()
        {
            try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; }
        }

        private static HashSet<int> ExcludeSet(params int[] offsets)
        {
            var set = new HashSet<int>();
            foreach (var o in offsets) if (o >= 0) set.Add(o);
            return set;
        }

        private string HintLine(DirectInputWheel w, HashSet<int> exclude)
        {
            string live = "";
            if (w != null)
            {
                float best = 0f; int bestOff = -1;
                foreach (var off in _offsets)
                {
                    if (exclude != null && exclude.Contains(off)) continue;
                    float baseN = _baseline.TryGetValue(off, out var b) ? b : 0.5f;
                    float mn = _minObs.TryGetValue(off, out var a1) ? a1 : baseN;
                    float mx = _maxObs.TryGetValue(off, out var a2) ? a2 : baseN;
                    float dev = Math.Max(Math.Abs(mn - baseN), Math.Abs(mx - baseN));
                    if (dev > best) { best = dev; bestOff = off; }
                }
                if (bestOff >= 0 && best >= 0.15f)
                    live = "  | moving now: " + DirectInputWheel.OffsetToConfigName(bestOff);
            }
            return "Keep holding it, then click the button below." + live;
        }

        // Draws the wizard and processes buttons. Returns Saved/Cancelled when finished.
        public WizardResult Draw(DirectInputWheel w, WheelConfig cfg)
        {
            var rect = Gui.CenteredPanel(620f, 630f);
            UnityEngine.GUI.Box(rect, "NIGHT-RUNNERS Wheel Support - Calibration");
            var result = WizardResult.None;

            // Check for completed file dialog from background thread
            if (_pendingImportPath != null && cfg != null)
            {
                string path = _pendingImportPath;
                _pendingImportPath = null;
                if (ConfigProfileManager.ImportProfileFromPath(cfg.ConfigFile, path, true, out var msg))
                {
                    cfg.Calibrated.Value = true;
                    cfg.Save();
                    ModLog.Info($"F10 Wizard imported profile: {msg}");
                    return WizardResult.Saved;
                }
                else
                {
                    _warn = "Import failed: " + msg;
                }
            }

            Gui.BeginArea(new UnityEngine.Rect(rect.x + 16f, rect.y + 32f, rect.width - 32f, rect.height - 44f));
            try
            {
                Gui.BeginV();
                try
                {
                    switch (_step)
                    {
                        case Step.Intro: result = DrawIntro(w, cfg); break;
                        case Step.SteerLeft: DrawSteerLeft(w); break;
                        case Step.SteerRight: DrawSteerRight(w); break;
                        case Step.Throttle: DrawPedal(w, "throttle", "accelerator", "throttle", "gas"); break;
                        case Step.Brake: DrawPedal(w, "brake", "brake"); break;
                        case Step.Clutch: DrawClutch(w); break;
                        case Step.Handbrake: DrawHandbrake(w); break;
                        case Step.ShiftUp: DrawShiftButton(w, true); break;
                        case Step.ShiftDown: DrawShiftButton(w, false); break;
                        case Step.Shifter: DrawShifter(w); break;
                        case Step.Summary: result = DrawSummary(w, cfg); break;
                    }

                    Gui.Space(8f);
                    string statusMsg = _fileDialogOpen ? "Waiting for file dialog in Windows..." : (string.IsNullOrEmpty(_warn) ? "" : "! " + _warn);
                    Gui.Label(statusMsg);
                    Gui.Flex();
                    Gui.BeginH();
                    if (Gui.Button("Cancel (ESC)")) result = WizardResult.Cancelled;
                    Gui.Flex();
                    Gui.Label(StepLabel());
                    Gui.EndH();
                }
                finally { Gui.EndV(); }
            }
            finally { Gui.EndArea(); }
            return result;
        }

        private string StepLabel()
        {
            switch (_step)
            {
                case Step.Intro: return "Step 1 / 10  (intro & profiles)";
                case Step.SteerLeft: return "Step 2 / 10  (steering, left)";
                case Step.SteerRight: return "Step 3 / 10  (steering, right)";
                case Step.Throttle: return "Step 4 / 10  (throttle)";
                case Step.Brake: return "Step 5 / 10  (brake)";
                case Step.Clutch: return "Step 6 / 10  (clutch, optional)";
                case Step.Handbrake: return "Step 7 / 10  (handbrake, optional)";
                case Step.ShiftUp: return "Step 8 / 10  (shift up paddle, optional)";
                case Step.ShiftDown: return "Step 9 / 10  (shift down paddle, optional)";
                case Step.Shifter: return "Step 10 / 10  (H-Shifter, optional)";
                case Step.Summary: return "Summary & Save";
                default: return "";
            }
        }

        private WizardResult DrawIntro(DirectInputWheel w, WheelConfig cfg)
        {
            Gui.Label("This wizard detects your wheel's axes and buttons, step by step.");
            Gui.Space(4f);
            Gui.Label("ALPHA NOTICE (v0.1.0-alpha): Early development build.");
            Gui.Label("Not all steering wheels or pedals are supported yet. You may encounter bugs,");
            Gui.Label("strange vehicle physics, or calibration quirks during early testing.");
            Gui.Space(6f);
            Gui.Label("1. Let go of everything first (wheel centered, pedals up).");
            Gui.Label("2. In each step: move the requested control ALL THE WAY, hold it, click Detect.");
            Gui.Label("3. Optional steps: clutch pedal, handbrake, paddle shifters, and H-shifter.");
            Gui.Label("Nothing is saved until you click Save at the end. ESC cancels.");
            Gui.Space(8f);
            if (w == null || w.AxisCount == 0)
                Gui.Label("Waiting for the wheel...");
            else
                Gui.Label($"Device: {w.DeviceName}  ({w.AxisCount} axes, {w.ButtonCount} buttons)");
            Gui.Space(8f);
            Gui.BeginH();
            if (Gui.Button("Start Manual Calibration (wheel centered)"))
            {
                CaptureBaseline(w);
                StartTracking(w);
                _step = Step.SteerLeft;
                _warn = "";
            }
            Gui.EndH();

            Gui.Space(12f);
            Gui.Label("-- Fast Track: Import or Load Existing Profile --");
            Gui.BeginH();
            if (Gui.Button("Import Profile (choose .json file...)") && !_fileDialogOpen)
            {
                _fileDialogOpen = true;
                _warn = "Opening file browser...";
                NativeDialogs.OpenFileAsync("Select Wheel Profile to Import", null, (chosenPath) =>
                {
                    _pendingImportPath = chosenPath;
                    _fileDialogOpen = false;
                });
            }
            Gui.EndH();

            var profileNames = ConfigProfileManager.GetProfileNames();
            if (profileNames.Count > 0)
            {
                Gui.Space(6f);
                Gui.Label($"Saved profiles ({profileNames.Count}) - click to load & apply directly:");
                string loadProfile = null;
                foreach (var prof in profileNames)
                {
                    Gui.BeginH();
                    Gui.Label($"  {prof}");
                    if (Gui.Button("Load & Apply"))
                    {
                        loadProfile = prof;
                    }
                    Gui.EndH();
                }

                if (loadProfile != null && cfg != null)
                {
                    if (ConfigProfileManager.ImportProfile(cfg.ConfigFile, loadProfile, out var msg))
                    {
                        cfg.Calibrated.Value = true;
                        cfg.Save();
                        ModLog.Info($"F10 Wizard loaded profile '{loadProfile}': {msg}");
                        return WizardResult.Saved;
                    }
                    else
                    {
                        _warn = "Failed to load profile: " + msg;
                    }
                }
            }

            return WizardResult.None;
        }

        private void DrawLiveAxes(DirectInputWheel w)
        {
            if (w == null) return;
            Gui.Space(6f);
            Gui.Label("Live axes:");
            foreach (var a in w.ReadAllAxes())
            {
                string mark = (a.Offset == _steerOffset) ? " <-- steering" : "";
                Gui.Label($"  {a.Config,-10} {Gui.Bar(a.Norm)} {a.Norm:F2}{mark}");
            }
        }

        private void DrawSteerLeft(DirectInputWheel w)
        {
            Gui.Label("STEERING - turn the wheel FULLY to the left and hold it there.");
            Gui.Label(HintLine(w, null));
            DrawLiveAxes(w);
            Gui.Space(6f);
            if (Gui.Button("Detect (holding full LEFT)"))
            {
                if (DetectMovedAxis(null, out var off, out var extreme, out var best, out _))
                {
                    _steerOffset = off;
                    _steerLeftNorm = extreme;
                    _steerAxis = DirectInputWheel.OffsetToConfigName(off);
                    _warn = "";
                    StartTracking(w);
                    _step = Step.SteerRight;
                }
                else if (best >= MoveThreshold)
                    _warn = "More than one control is deflected (device wake-up). Keep only the wheel at full LEFT and click Detect again.";
                else
                    _warn = "No movement seen yet. Turn the wheel all the way to the LEFT and click Detect again.";
            }
        }

        private void DrawSteerRight(DirectInputWheel w)
        {
            Gui.Label($"Good - steering axis found ({_steerAxis}). Now turn the wheel FULLY to the RIGHT and hold it.");
            DrawLiveAxes(w);
            Gui.Space(6f);
            if (Gui.Button("Detect (holding full RIGHT)"))
            {
                if (w == null || _steerOffset < 0) { _warn = "No wheel signal. Try again."; return; }
                float baseN = _baseline.TryGetValue(_steerOffset, out var b) ? b : 0.5f;
                float mn = _minObs.TryGetValue(_steerOffset, out var a1) ? a1 : baseN;
                float mx = _maxObs.TryGetValue(_steerOffset, out var a2) ? a2 : baseN;
                float rightNorm = Math.Abs(mx - _steerLeftNorm) >= Math.Abs(mn - _steerLeftNorm) ? mx : mn;
                _steerRightNorm = rightNorm;
                _invSteer = _steerLeftNorm > _steerRightNorm;
                if (Math.Abs(_steerLeftNorm - _steerRightNorm) < MoveThreshold)
                {
                    _warn = "Left and right look too similar. Turn the wheel all the way each way. Restarting steering.";
                    _steerOffset = -1; _steerAxis = null;
                    StartTracking(w);
                    _step = Step.SteerLeft;
                    return;
                }
                _warn = "";
                StartTracking(w);
                _step = Step.Throttle;
            }
        }

        private void DrawPedal(DirectInputWheel w, string label, params string[] keywords)
        {
            Gui.Label($"{label.ToUpperInvariant()} - press the {label} pedal all the way down and hold it.");
            var exclude = _step == Step.Throttle ? ExcludeSet(_steerOffset) : ExcludeSet(_steerOffset, _throttleOffset);
            Gui.Label(HintLine(w, exclude));
            DrawLiveAxes(w);
            Gui.Space(6f);
            if (Gui.Button($"Detect {label}"))
            {
                if (DetectMovedAxis(exclude, out var off, out var extreme))
                {
                    string axis = DirectInputWheel.OffsetToConfigName(off);
                    float baseN = _baseline.TryGetValue(off, out var b) ? b : 0.5f;
                    bool invert = extreme < baseN;
                    if (_step == Step.Throttle) { _throttleOffset = off; _throttleAxis = axis; _invThrottle = invert; _warn = ""; StartTracking(w); _step = Step.Brake; }
                    else if (_step == Step.Brake) { _brakeOffset = off; _brakeAxis = axis; _invBrake = invert; _warn = ""; StartTracking(w); _step = Step.Clutch; }
                }
                else _warn = $"No clear {label} movement. Press ONLY the {label} pedal all the way down and click Detect again.";
            }
        }

        private void DrawClutch(DirectInputWheel w)
        {
            Gui.Label("CLUTCH (optional) - press the clutch pedal all the way down, or skip this step.");
            Gui.Label(HintLine(w, ExcludeSet(_steerOffset, _throttleOffset, _brakeOffset)));
            DrawLiveAxes(w);
            Gui.Space(6f);
            Gui.BeginH();
            if (Gui.Button("Detect clutch"))
            {
                if (DetectMovedAxis(ExcludeSet(_steerOffset, _throttleOffset, _brakeOffset), out var off, out var extreme))
                {
                    _clutchOffset = off;
                    _clutchAxis = DirectInputWheel.OffsetToConfigName(off);
                    float baseN = _baseline.TryGetValue(off, out var b) ? b : 0.5f;
                    _invClutch = extreme < baseN;
                    _clutchSet = true;
                    EnterButtonStep(Step.Handbrake);
                }
                else _warn = "Not enough movement. Press the clutch all the way down, or skip.";
            }
            if (Gui.Button("Skip clutch"))
            {
                _clutchSet = false; _clutchAxis = "";
                EnterButtonStep(Step.Handbrake);
            }
            Gui.EndH();
        }

        private void DrawHandbrake(DirectInputWheel w)
        {
            Gui.Label("HANDBRAKE (optional) - press the button you want to use as handbrake.");
            Gui.Label(_capturedButton >= 0
                ? $"Captured: Button{_capturedButton}"
                : (_btnArmed ? "Waiting for a button press..." : "Release all buttons first..."));
            Gui.Space(6f);
            Gui.BeginH();
            if (_capturedButton >= 0 && Gui.Button($"Use Button{_capturedButton}"))
            {
                _handbrakeButton = _capturedButton; EnterButtonStep(Step.ShiftUp);
            }
            if (Gui.Button("Skip handbrake"))
            {
                _handbrakeButton = -1; EnterButtonStep(Step.ShiftUp);
            }
            if (_capturedButton >= 0 && Gui.Button("Clear")) { _capturedButton = -1; _btnArmed = false; }
            Gui.EndH();
        }

        private void DrawShiftButton(DirectInputWheel w, bool isUp)
        {
            string what = isUp ? "SHIFT UP PADDLE" : "SHIFT DOWN PADDLE";
            Gui.Label($"{what} (optional) - press the paddle/button for shifting {(isUp ? "up" : "down")}.");
            Gui.Label("Note: sequential shifting works alongside H-shifter.");
            Gui.Label(_capturedButton >= 0
                ? $"Captured: Button{_capturedButton}"
                : (_btnArmed ? "Waiting for a button press..." : "Release all buttons first..."));
            Gui.Space(6f);
            Gui.BeginH();
            if (_capturedButton >= 0 && Gui.Button($"Use Button{_capturedButton}"))
            {
                if (isUp) { _shiftUpButton = _capturedButton; EnterButtonStep(Step.ShiftDown); }
                else { _shiftDownButton = _capturedButton; _warn = ""; _step = Step.Shifter; }
            }
            if (Gui.Button($"Skip shift {(isUp ? "up" : "down")}"))
            {
                if (isUp) { _shiftUpButton = -1; EnterButtonStep(Step.ShiftDown); }
                else { _shiftDownButton = -1; _warn = ""; _step = Step.Shifter; }
            }
            if (_capturedButton >= 0 && Gui.Button("Clear")) { _capturedButton = -1; _btnArmed = false; }
            Gui.EndH();
        }

        private void DrawShifter(DirectInputWheel w)
        {
            Gui.Label("H-SHIFTER (optional) - Map physical gear positions for manual transmission.");
            int currentBtn = w?.FirstPressedButton() ?? -1;
            string heldText = currentBtn >= 0 ? $"Currently held button: Button{currentBtn}" : "No shifter button held (lever in neutral)";
            Gui.Label(heldText);
            Gui.Space(4f);

            ShifterRow("Gear 1", ref _shifterG1, currentBtn);
            ShifterRow("Gear 2", ref _shifterG2, currentBtn);
            ShifterRow("Gear 3", ref _shifterG3, currentBtn);
            ShifterRow("Gear 4", ref _shifterG4, currentBtn);
            ShifterRow("Gear 5", ref _shifterG5, currentBtn);
            ShifterRow("Gear 6", ref _shifterG6, currentBtn);
            ShifterRow("Reverse", ref _shifterGR, currentBtn);

            Gui.Space(6f);
            Gui.BeginH();
            if (Gui.Button("Continue to Summary"))
            {
                _warn = "";
                _step = Step.Summary;
            }
            if (Gui.Button("Skip / Clear Shifter"))
            {
                _shifterG1 = _shifterG2 = _shifterG3 = _shifterG4 = _shifterG5 = _shifterG6 = _shifterGR = -1;
                _warn = "";
                _step = Step.Summary;
            }
            Gui.EndH();
        }

        private void ShifterRow(string label, ref int gearBtn, int currentPressedBtn)
        {
            Gui.BeginH();
            string btnText = gearBtn >= 0 ? $"Button{gearBtn}" : "(unmapped)";
            Gui.Label($"{label,-10}: {btnText,-12}");
            if (currentPressedBtn >= 0 && Gui.Button($"Set to Button{currentPressedBtn}"))
            {
                gearBtn = currentPressedBtn;
            }
            if (gearBtn >= 0 && Gui.Button("Clear"))
            {
                gearBtn = -1;
            }
            Gui.EndH();
        }

        private WizardResult DrawSummary(DirectInputWheel w, WheelConfig cfg)
        {
            Gui.Label("Almost done. Detected setup:");
            Gui.Label($"  Steering : {_steerAxis}   invert={_invSteer}");
            Gui.Label($"  Throttle : {_throttleAxis}   invert={_invThrottle}");
            Gui.Label($"  Brake    : {_brakeAxis}   invert={_invBrake}");
            Gui.Label($"  Clutch   : {(_clutchSet ? _clutchAxis + "  invert=" + _invClutch : "(none)")}");
            Gui.Label($"  Handbrake: {(_handbrakeButton >= 0 ? "Button" + _handbrakeButton : "(none)")}");
            Gui.Label($"  Shift up : {(_shiftUpButton >= 0 ? "Button" + _shiftUpButton : "(none)")}");
            Gui.Label($"  Shift dn : {(_shiftDownButton >= 0 ? "Button" + _shiftDownButton : "(none)")}");

            bool hasShifter = _shifterG1 >= 0 || _shifterG2 >= 0 || _shifterG3 >= 0 ||
                              _shifterG4 >= 0 || _shifterG5 >= 0 || _shifterG6 >= 0 || _shifterGR >= 0;
            string shDesc = hasShifter
                ? $"1:B{_shifterG1}, 2:B{_shifterG2}, 3:B{_shifterG3}, 4:B{_shifterG4}, 5:B{_shifterG5}, 6:B{_shifterG6}, R:B{_shifterGR}"
                : "(none / paddles only)";
            Gui.Label($"  H-Shifter: {shDesc}");

            Gui.Space(10f);
            Gui.BeginH();
            var res = WizardResult.None;
            if (Gui.Button("Save & finish"))
            {
                ApplyToConfig(cfg);
                res = WizardResult.Saved;
            }
            if (Gui.Button("Start over")) { Begin(w, cfg); }
            Gui.EndH();
            return res;
        }

        private void ApplyToConfig(WheelConfig cfg)
        {
            if (cfg == null) return;
            if (!string.IsNullOrEmpty(_steerAxis)) { cfg.SteerAxis.Value = _steerAxis; cfg.InvertSteer.Value = _invSteer; }
            if (!string.IsNullOrEmpty(_throttleAxis)) { cfg.ThrottleAxis.Value = _throttleAxis; cfg.InvertThrottle.Value = _invThrottle; }
            if (!string.IsNullOrEmpty(_brakeAxis)) { cfg.BrakeAxis.Value = _brakeAxis; cfg.InvertBrake.Value = _invBrake; }
            cfg.CombinedPedals.Value = false;
            if (_clutchSet) { cfg.ClutchAxis.Value = _clutchAxis; cfg.InvertClutch.Value = _invClutch; }
            else cfg.ClutchAxis.Value = "";
            cfg.HandbrakeAxis.Value = "";
            cfg.HandbrakeButton.Value = _handbrakeButton;
            cfg.ShiftUpButton.Value = _shiftUpButton;
            cfg.ShiftDownButton.Value = _shiftDownButton;
            cfg.ShifterGear1Button.Value = _shifterG1;
            cfg.ShifterGear2Button.Value = _shifterG2;
            cfg.ShifterGear3Button.Value = _shifterG3;
            cfg.ShifterGear4Button.Value = _shifterG4;
            cfg.ShifterGear5Button.Value = _shifterG5;
            cfg.ShifterGear6Button.Value = _shifterG6;
            cfg.ShifterReverseButton.Value = _shifterGR;
            cfg.Calibrated.Value = true;
            cfg.Save();
            ModLog.Info($"Calibration saved: Steer={_steerAxis}(inv={_invSteer}) Throttle={_throttleAxis} Brake={_brakeAxis} Clutch={(_clutchSet ? _clutchAxis : "-")} HandbrakeBtn={_handbrakeButton} ShiftUp={_shiftUpButton} ShiftDown={_shiftDownButton} Shifter=[1:{_shifterG1},2:{_shifterG2},3:{_shifterG3},4:{_shifterG4},5:{_shifterG5},6:{_shifterG6},R:{_shifterGR}]");
        }
    }
}
