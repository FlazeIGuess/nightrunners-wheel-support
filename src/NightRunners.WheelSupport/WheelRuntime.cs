using System;
using NightRunners.WheelSupport.Game;
using NightRunners.WheelSupport.Patches;
using NightRunners.WheelSupport.Ui;
using UnityEngine;

namespace NightRunners.WheelSupport
{
    // Il2Cpp MonoBehaviour that runs every frame: poll the wheel, read the car state, compute FFB,
    // and drive the calibration wizard / settings menu (IMGUI). The actual input injection happens
    // in the Harmony postfix on CarLocalCustom.Inputs() and - inlining-immune - in LateUpdate;
    // this runtime provides the current axis values for that.
    public sealed class WheelRuntime : MonoBehaviour
    {
        public WheelRuntime(IntPtr ptr) : base(ptr) { }

        private readonly ModUi _ui = new ModUi();

        private bool _initTried;
        private bool _initDone;
        private int _initAttempts;
        private const int MaxInitAttempts = 600; // ~10 s at 60 FPS
        private bool _lastCrashFlag;
        private bool _lastShiftUp;
        private bool _lastShiftDown;
        private bool _lastAnyOpen;

        // H-Shifter state (-1 = Reverse, 0 = Neutral, 1..6 = Gears 1..6)
        private int _currentGear = 0;
        private int _desiredGear = 0;

        public void Update()
        {
            try { UpdateInner(); }
            catch (Exception e)
            {
                // Warn (not verbose-only) so a persistent per-frame failure is visible by default.
                ModLog.WarnThrottled("update", NowSafe(), 5f, $"Update error: {e.Message}");
                Neutralize();
            }
        }

        private void UpdateInner()
        {
            var cfg = ModContext.Config;
            if (cfg == null) return;

            if (!cfg.Enabled.Value)
            {
                Neutralize();
                return;
            }

            ModLog.SetVerbose(cfg.VerboseLogging.Value);
            float now = Time.realtimeSinceStartup;
            var w = ModContext.Wheel;

            // --- deferred device initialization ---
            if (!_initDone)
            {
                if (_initAttempts++ <= MaxInitAttempts && (_initAttempts % 30 == 1) && w != null)
                {
                    if (w.TryInit(cfg))
                    {
                        _initDone = true;
                        ModContext.ComputeInjectionFlags();
                        LogStartupSummary();
                    }
                }
                else if (_initAttempts == MaxInitAttempts + 1 && !_initTried)
                {
                    _initTried = true;
                    ModLog.Warn("No wheel initialized (timeout). Original controls remain active.");
                }
            }

            bool ready = _initDone && w != null && w.Available;

            // Poll (needed both for driving and for live UI display).
            bool polled = false;
            if (ready) polled = w.Poll(cfg);
            if (ready && !polled) Neutralize(); // device lost -> hand control back to the game

            // --- UI (hotkeys, auto-open, overlays). Runs every frame. ---
            _ui.HandleFrame(w, cfg, ready && polled, now);
            if (_ui.AnyOpen)
            {
                _lastAnyOpen = true;
                return; // overlay owns input + FFB; nothing else this frame
            }

            if (!ready || !polled) { _lastAnyOpen = false; return; }

            // The overlay just closed this frame: absorb the current shift-button state so a paddle
            // still held from before/while the overlay was open is not treated as a fresh edge.
            if (_lastAnyOpen)
            {
                _lastShiftUp = cfg.ShiftUpButton.Value >= 0 && w.GetButton(cfg.ShiftUpButton.Value);
                _lastShiftDown = cfg.ShiftDownButton.Value >= 0 && w.GetButton(cfg.ShiftDownButton.Value);
                _lastAnyOpen = false;
            }

            // --- normal operation: feed axes, compute injection, drive FFB ---
            ModContext.AxSteer = w.Steer;
            ModContext.AxThrottle = w.Throttle;
            ModContext.AxBrake = w.Brake;
            ModContext.AxClutch = w.Clutch;

            float handbrake = w.Handbrake;
            if (cfg.HandbrakeButton.Value >= 0 && w.GetButton(cfg.HandbrakeButton.Value)) handbrake = 1f;
            ModContext.AxHandbrake = handbrake;
            ModContext.AxNos = (cfg.NosButton.Value >= 0 && w.GetButton(cfg.NosButton.Value)) ? 1f : 0f;

            ModContext.ComputeInjectionFlags();
            HandleHShifter(w, cfg);
            HandleShiftButtons();

            if (ModLog.Verbose)
            {
                ModLog.DebugThrottled("axes", now, 0.5f,
                    $"axes raw[{w.RawAxesDebug()}] -> Steer={w.Steer:F2} Throttle={w.Throttle:F2} Brake={w.Brake:F2} Clutch={w.Clutch:F2} Handbrake={ModContext.AxHandbrake:F2} | inject S={ModContext.InjectSteer} T={ModContext.InjectThrottle} B={ModContext.InjectBrake}");
            }

            var state = PlayerCar.Read();
            if (state.Valid)
            {
                if (state.PlayerHasCrashed && !_lastCrashFlag)
                    ModContext.Ffb?.TriggerCrash(Math.Max(state.Speed, cfg.CrashMinImpulse.Value + 0.01f), now);
                _lastCrashFlag = state.PlayerHasCrashed;

                if (ModLog.Verbose)
                    ModLog.DebugThrottled("carstate", now, 1.0f,
                        $"car: speed={state.Speed:F1} rpm={state.EngineRpm:F0}/{state.MaxRpm:F0} steerAngle={state.SteerAngle:F1} fSlip={state.FrontSlip:F2} rSlip={state.RearSlip:F2} drift={state.Drifting} grounded={!state.NotGrounded}");
            }

            ModContext.Ffb?.Update(in state, ModContext.AxSteer, now);
        }

        private void HandleHShifter(Input.DirectInputWheel w, Config.WheelConfig cfg)
        {
            try
            {
                if (!cfg.UseWheelInput.Value) return;

                // 1) Read physical H-shifter buttons (if any are mapped)
                bool physicalMapped = cfg.ShifterGear1Button.Value >= 0 ||
                                      cfg.ShifterGear2Button.Value >= 0 ||
                                      cfg.ShifterGear3Button.Value >= 0 ||
                                      cfg.ShifterGear4Button.Value >= 0 ||
                                      cfg.ShifterGear5Button.Value >= 0 ||
                                      cfg.ShifterGear6Button.Value >= 0 ||
                                      cfg.ShifterReverseButton.Value >= 0;

                if (physicalMapped && w != null)
                {
                    if (cfg.ShifterGear1Button.Value >= 0 && w.GetButton(cfg.ShifterGear1Button.Value)) _desiredGear = 1;
                    else if (cfg.ShifterGear2Button.Value >= 0 && w.GetButton(cfg.ShifterGear2Button.Value)) _desiredGear = 2;
                    else if (cfg.ShifterGear3Button.Value >= 0 && w.GetButton(cfg.ShifterGear3Button.Value)) _desiredGear = 3;
                    else if (cfg.ShifterGear4Button.Value >= 0 && w.GetButton(cfg.ShifterGear4Button.Value)) _desiredGear = 4;
                    else if (cfg.ShifterGear5Button.Value >= 0 && w.GetButton(cfg.ShifterGear5Button.Value)) _desiredGear = 5;
                    else if (cfg.ShifterGear6Button.Value >= 0 && w.GetButton(cfg.ShifterGear6Button.Value)) _desiredGear = 6;
                    else if (cfg.ShifterReverseButton.Value >= 0 && w.GetButton(cfg.ShifterReverseButton.Value)) _desiredGear = -1;
                    else _desiredGear = 0; // Neutral when no gear button is held on a physical H-shifter
                }

                // 2) Keyboard emulation for H-shifter (Keys 1-6, R, 0/N)
                if (cfg.KeyboardShifter.Value && !_ui.AnyOpen)
                {
                    if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha1) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad1)) _desiredGear = 1;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha2) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad2)) _desiredGear = 2;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha3) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad3)) _desiredGear = 3;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha4) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad4)) _desiredGear = 4;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha5) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad5)) _desiredGear = 5;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha6) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad6)) _desiredGear = 6;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.R) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadPeriod)) _desiredGear = -1;
                    else if (UnityEngine.Input.GetKeyDown(KeyCode.Alpha0) || UnityEngine.Input.GetKeyDown(KeyCode.Keypad0) || UnityEngine.Input.GetKeyDown(KeyCode.N)) _desiredGear = 0;
                }

                // 3) Clutch pedal state
                bool clutchDepressed = ModContext.InjectClutch && (ModContext.AxClutch >= 0.45f);

                // If clutch is depressed, car enters neutral (free engine rev, zero drivetrain drag)
                if (clutchDepressed)
                {
                    ModContext.ForceNeutral = true;
                }

                // 4) Gear change transition
                if (_desiredGear != _currentGear)
                {
                    bool canShift = !cfg.RequireClutchToShift.Value || clutchDepressed || !ModContext.InjectClutch;
                    if (canShift)
                    {
                        _currentGear = _desiredGear;
                        var car = PlayerCar.Get();
                        if (car != null)
                        {
                            if (_currentGear == 0) // Neutral
                            {
                                car.NGear = true;
                                car.direction = 0;
                                ModLog.Info("H-Shifter: Neutral selected");
                            }
                            else if (_currentGear == -1) // Reverse
                            {
                                car.StartCoroutine(car.ChangeGear(-1));
                                car.direction = -1;
                                car.currentGear = 0;
                                car.NGear = clutchDepressed;
                                ModLog.Info("H-Shifter: Reverse engaged");
                            }
                            else // Gears 1..6
                            {
                                int rccIndex = _currentGear - 1;
                                car.StartCoroutine(car.ChangeGear(rccIndex));
                                car.direction = 1;
                                car.currentGear = rccIndex;
                                car.NGear = clutchDepressed;
                                ModLog.Info($"H-Shifter: Gear {_currentGear} engaged");
                            }
                        }
                    }
                }

                // 5) When clutch pedal is released (or foot is off pedal):
                if (!clutchDepressed)
                {
                    if (_currentGear == 0)
                    {
                        // In Neutral: keep car in neutral even when clutch is up
                        ModContext.ForceNeutral = true;
                        var car = PlayerCar.Get();
                        if (car != null)
                        {
                            car.NGear = true;
                            car.direction = 0;
                            car.clutchInput = 1f;
                        }
                    }
                    else
                    {
                        // In Gear: drive engaged
                        ModContext.ForceNeutral = false;
                        var car = PlayerCar.Get();
                        if (car != null)
                        {
                            car.NGear = false;
                            car.direction = (_currentGear == -1) ? -1 : 1;
                            car.currentGear = (_currentGear == -1) ? 0 : (_currentGear - 1);
                            if (ModContext.InjectClutch)
                            {
                                car.clutchInput = ModContext.AxClutch;
                            }
                        }
                    }
                }

                ModContext.CurrentGearDisplay = _currentGear;
            }
            catch (Exception e)
            {
                ModLog.DebugThrottled("hshifter", NowSafe(), 2f, $"H-Shifter error: {e.Message}");
            }
        }

        // Deliberately parameterless: Il2CppInterop scans MonoBehaviour methods and warns about
        // custom parameter types ("unsupported parameter"), so this reads the shared state itself.
        private void HandleShiftButtons()
        {
            try
            {
                var w = ModContext.Wheel;
                var cfg = ModContext.Config;
                if (w == null || cfg == null) return;
                if (!cfg.UseWheelInput.Value) return; // FFB-only mode leaves gear control to the game
                bool up = cfg.ShiftUpButton.Value >= 0 && w.GetButton(cfg.ShiftUpButton.Value);
                bool down = cfg.ShiftDownButton.Value >= 0 && w.GetButton(cfg.ShiftDownButton.Value);
                if (up && !_lastShiftUp)
                {
                    if (_desiredGear == -1) _desiredGear = 0;
                    else if (_desiredGear == 0) _desiredGear = 1;
                    else if (_desiredGear < 6) _desiredGear++;
                    ModLog.Info($"Paddle shift up -> Gear {_desiredGear}");
                }
                if (down && !_lastShiftDown)
                {
                    if (_desiredGear > 1) _desiredGear--;
                    else if (_desiredGear == 1) _desiredGear = 0;
                    else if (_desiredGear == 0) _desiredGear = -1;
                    ModLog.Info($"Paddle shift down -> Gear {_desiredGear}");
                }
                _lastShiftUp = up;
                _lastShiftDown = down;
            }
            catch (Exception e) { ModLog.DebugThrottled("shift", NowSafe(), 2f, $"Shift error: {e.Message}"); }
        }

        // Second, inlining-immune injection path (after all Update()s).
        public void LateUpdate()
        {
            try
            {
                if (!_initDone || _ui.AnyOpen) return;
                var cfg = ModContext.Config;
                if (cfg == null || !cfg.Enabled.Value) return;
                InputInjector.ApplyToPlayer();
            }
            catch (Exception e)
            {
                ModLog.WarnThrottled("lateupdate", NowSafe(), 5f, $"LateUpdate error: {e.Message}");
            }
        }

        public void OnGUI()
        {
            try { _ui.OnGUI(ModContext.Wheel, ModContext.Config); }
            catch (Exception e) { ModLog.WarnThrottled("ongui", NowSafe(), 5f, $"OnGUI error: {e.Message}"); }
        }

        // Injection off, axes zeroed, FFB neutral. Hands control back to the game immediately.
        private static void Neutralize()
        {
            ModContext.InputInjectionEnabled = false;
            ModContext.InjectSteer = ModContext.InjectThrottle = ModContext.InjectBrake = ModContext.InjectHandbrake = ModContext.InjectNos = false;
            ModContext.AxSteer = ModContext.AxThrottle = ModContext.AxBrake = ModContext.AxHandbrake = ModContext.AxNos = 0f;
            try { ModContext.Ffb?.SilenceAll(); } catch { }
        }

        private static float NowSafe()
        {
            try { return Time.realtimeSinceStartup; } catch { return 0f; }
        }

        public void OnDestroy()
        {
            try { ModContext.Ffb?.SilenceAll(); } catch { }
            try { ModContext.Wheel?.Dispose(); } catch { }
        }

        private void LogStartupSummary()
        {
            var w = ModContext.Wheel;
            var c = ModContext.Config;
            ModLog.Info("========================================");
            ModLog.Info($"Wheel ready: {w.DeviceName}");
            ModLog.Info($"  axes={w.AxisCount} buttons={w.ButtonCount} | FFB={(w.FfbAvailable ? "active" : "NOT available")}");
            ModLog.Info($"  input injection: {(c.UseWheelInput.Value ? "ON" : "off (FFB only)")} | Steer={ModContext.InjectSteer} Throttle={ModContext.InjectThrottle} Brake={ModContext.InjectBrake} Handbrake={ModContext.InjectHandbrake}");
            if (!c.Calibrated.Value)
                ModLog.Info($"  Not calibrated yet -> the calibration wizard will open. Or press {c.CalibrateHotkey.Value} anytime.");
            ModLog.Info($"  Hotkeys: {c.CalibrateHotkey.Value} = calibration wizard, {c.MenuHotkey.Value} = settings menu.");
            ModLog.Info("========================================");
        }
    }
}
