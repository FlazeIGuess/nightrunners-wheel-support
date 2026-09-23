using System;
using NightRunners.WheelSupport.Config;
using NightRunners.WheelSupport.Input;
using UnityEngine;

namespace NightRunners.WheelSupport.Ui
{
    // Orchestrates the calibration wizard and settings menu: hotkeys, auto-open on first start,
    // and suspending car injection while an overlay is open. All entry points are wrapped in
    // try/catch by the caller (WheelRuntime); this class also guards its own hot paths.
    public sealed class ModUi
    {
        private readonly CalibrationWizard _wizard = new CalibrationWizard();
        private readonly SettingsMenu _menu = new SettingsMenu();

        public bool WizardOpen { get; private set; }
        public bool MenuOpen { get; private set; }
        public bool AnyOpen => WizardOpen || MenuOpen;

        private bool _autoWizardDone;

        // Called from Update (main thread). wheelReady = wheel initialized and available.
        public void HandleFrame(DirectInputWheel wheel, WheelConfig cfg, bool wheelReady, float now)
        {
            // Auto-open the wizard once, on the first start after (re)install, when uncalibrated.
            if (!_autoWizardDone && wheelReady && cfg != null && !cfg.Calibrated.Value && !AnyOpen)
            {
                _autoWizardDone = true;
                OpenWizard(wheel);
                ModLog.Info("First start without calibration -> opening the calibration wizard. Press ESC to skip.");
            }

            HandleHotkeys(wheel, cfg, now);

            ModContext.UiSuspendInjection = AnyOpen;

            if (AnyOpen)
            {
                // Suspend injection directly (bulletproof against a one-frame lag) and zero the axes.
                ModContext.InputInjectionEnabled = false;
                ModContext.AxSteer = ModContext.AxThrottle = ModContext.AxBrake = ModContext.AxHandbrake = ModContext.AxNos = 0f;

                if (WizardOpen) _wizard.Tick(wheel);
                if (MenuOpen) _menu.Tick(wheel, now);
                if (WizardOpen && wheel != null && wheel.FfbAvailable)
                {
                    // keep the wheel quiet during calibration
                    wheel.SetConstantForce(0f);
                    wheel.SetVibration(0f, 20f);
                }
                // Keep the cursor usable while an overlay is open (the game may re-lock it each frame).
                try { Cursor.visible = true; } catch { }
                try { Cursor.lockState = CursorLockMode.None; } catch { }
            }
        }

        // true when the mod (not the game) currently owns the wheel FFB.
        public bool OwnsFfb => AnyOpen;

        private void HandleHotkeys(DirectInputWheel wheel, WheelConfig cfg, float now)
        {
            if (cfg == null) return;
            try
            {
                if (GetKeyDown(cfg.CalibrateHotkey.Value, KeyCode.F10))
                {
                    if (WizardOpen) CloseAll();
                    else OpenWizard(wheel);
                }
                if (GetKeyDown(cfg.MenuHotkey.Value, KeyCode.F9))
                {
                    if (MenuOpen) CloseAll();
                    else OpenMenu(wheel);
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
                {
                    if (cfg != null && cfg.ShowWatermark != null)
                    {
                        cfg.ShowWatermark.Value = !cfg.ShowWatermark.Value;
                        cfg.Save();
                        ModLog.Info($"Watermark HUD display: {(cfg.ShowWatermark.Value ? "ON" : "OFF")}");
                    }
                }
                if (AnyOpen && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                    CloseAll();
            }
            catch (Exception e) { ModLog.DebugThrottled("hotkey", now, 3f, $"Hotkey error: {e.Message}"); }
        }

        private static GUIStyle _wmTitleStyle;
        private static GUIStyle _wmKeyStyle;

        private void DrawWatermark(WheelConfig cfg)
        {
            if (cfg == null || cfg.ShowWatermark == null || !cfg.ShowWatermark.Value) return;

            float w = 320f;
            float h = 44f;
            float sw = 1920f;
            try { sw = UnityEngine.Screen.width; } catch { }
            if (sw < 400f) sw = 1920f;
            float x = sw - w - 12f;
            float y = 12f;

            UnityEngine.GUI.Box(new UnityEngine.Rect(x, y, w, h), "");

            if (_wmTitleStyle == null)
            {
                _wmTitleStyle = new GUIStyle(UnityEngine.GUI.skin.label)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft
                };
                _wmTitleStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 0.95f);
            }

            if (_wmKeyStyle == null)
            {
                _wmKeyStyle = new GUIStyle(UnityEngine.GUI.skin.label)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Normal,
                    alignment = TextAnchor.UpperLeft
                };
                _wmKeyStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f, 0.85f);
            }

            UnityEngine.GUI.Label(new UnityEngine.Rect(x + 8f, y + 4f, w - 16f, 18f), $"NIGHT-RUNNERS Wheel Support v{WheelSupportPlugin.PluginVersion}", _wmTitleStyle);
            UnityEngine.GUI.Label(new UnityEngine.Rect(x + 8f, y + 22f, w - 16f, 18f), "[F9] Settings  |  [F10] Calibration  |  [F8] Hide", _wmKeyStyle);
        }

        public void OnGUI(DirectInputWheel wheel, WheelConfig cfg)
        {
            try
            {
                // The watermark is cosmetic; a failure here must never close an open overlay
                // or take the rest of the UI down with it.
                try { DrawWatermark(cfg); }
                catch (Exception wmEx) { ModLog.DebugThrottled("watermark", NowSafe(), 10f, $"Watermark draw error: {wmEx.Message}"); }

                if (WizardOpen)
                {
                    var r = _wizard.Draw(wheel, cfg);
                    if (r == WizardResult.Saved) { CloseAll(); ModLog.Info("Calibration complete."); ModContext.ComputeInjectionFlags(); }
                    else if (r == WizardResult.Cancelled) { CloseAll(); ModLog.Info("Calibration cancelled."); }
                }
                else if (MenuOpen)
                {
                    _menu.Draw(wheel, cfg);
                    if (_menu.RecalibrateRequested) { MenuOpen = false; OpenWizard(wheel); }
                    else if (_menu.ConsumeCloseRequest()) CloseAll();
                }
            }
            catch (Exception e)
            {
                // Never let an IMGUI exception break the game; close the overlay on error.
                // Warn (not verbose-only) so a persistent layout desync is visible by default.
                ModLog.WarnThrottled("ongui", NowSafe(), 5f, $"OnGUI error (overlay closed): {e.Message}");
                CloseAll();
            }
        }

        private static float NowSafe()
        {
            try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; }
        }

        private void OpenWizard(DirectInputWheel wheel)
        {
            MenuOpen = false;
            _wizard.Begin(wheel, ModContext.Config);
            WizardOpen = true;
        }

        private void OpenMenu(DirectInputWheel wheel)
        {
            WizardOpen = false;
            _menu.Begin(wheel);
            MenuOpen = true;
            // Avoid a synchronous file write per frame while dragging sliders; CloseAll() saves.
            try { ModContext.Config?.SetAutoSave(false); } catch { }
        }

        public void CloseAll()
        {
            bool wasOpen = WizardOpen || MenuOpen;
            WizardOpen = false;
            MenuOpen = false;
            ModContext.UiSuspendInjection = false;
            ModContext.ComputeInjectionFlags();
            // Re-enable auto-save and persist any live edits made in the overlay.
            if (wasOpen)
            {
                try { ModContext.Config?.SetAutoSave(true); } catch { }
                try { ModContext.Config?.Save(); } catch { }
            }
        }

        private static bool GetKeyDown(string keyName, KeyCode fallback)
        {
            KeyCode kc = fallback;
            if (!string.IsNullOrEmpty(keyName) && Enum.TryParse<KeyCode>(keyName, true, out var parsed))
                kc = parsed;
            return UnityEngine.Input.GetKeyDown(kc);
        }
    }
}
