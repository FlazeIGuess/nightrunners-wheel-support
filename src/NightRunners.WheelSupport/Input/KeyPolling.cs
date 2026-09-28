using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NightRunners.WheelSupport.Input
{
    // Keyboard input abstraction. The PRIVATE ALPHA build ships with Unity's legacy input module
    // (UnityEngine.InputLegacyModule), the PROLOGUE build does not (it runs on Rewired and the
    // module was stripped). A direct reference to UnityEngine.Input fails at JIT time on builds
    // without that module and kills the whole Update loop (no hotkeys, cursor stays locked, no
    // FFB updates). This helper resolves UnityEngine.Input via reflection when the module exists
    // and falls back to a Win32 key poll (GetAsyncKeyState) when it does not, so hotkeys work
    // identically in both builds.
    internal static class KeyPolling
    {
        private static bool _init;
        private static bool _useWin32;
        private static MethodInfo _getKeyDown; // UnityEngine.Input.GetKeyDown(KeyCode or int)
        private static bool _argIsEnum;
        private static uint _myPid;
        private static readonly HashSet<int> _heldKeys = new HashSet<int>();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public static bool GetKeyDown(KeyCode key)
        {
            if (!_init) Init();

            if (!_useWin32 && _getKeyDown != null)
            {
                try
                {
                    object arg = _argIsEnum ? (object)key : (object)(int)key;
                    return (bool)_getKeyDown.Invoke(null, new[] { arg });
                }
                catch (Exception e)
                {
                    ModLog.Warn($"UnityEngine.Input failed at runtime, switching to Win32 key polling: {e.Message}");
                    _useWin32 = true;
                }
            }

            return GetKeyDownWin32(key);
        }

        private static bool GetKeyDownWin32(KeyCode key)
        {
            // Only react while this process owns the foreground window; keys pressed in other
            // applications must never trigger the mod's hotkeys or shifter keys.
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != _myPid) return false;
            }
            catch { }

            int vk = ToVirtualKey(key);
            if (vk == 0) return false;

            bool down;
            try { down = (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch { return false; }

            lock (_heldKeys)
            {
                if (down && !_heldKeys.Contains(vk))
                {
                    _heldKeys.Add(vk);
                    return true;
                }
                if (!down) _heldKeys.Remove(vk);
                return false;
            }
        }

        private static void Init()
        {
            _init = true;
            try { _myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }

            try
            {
                Assembly asm = null;

                // 1) Already loaded into this app domain (normal case when the module exists).
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == "UnityEngine.InputLegacyModule") { asm = a; break; }
                }

                // 2) Ask the loader to resolve it (BepInEx/Il2CppInterop serves interop assemblies on demand).
                if (asm == null)
                {
                    try { asm = Assembly.Load(new AssemblyName("UnityEngine.InputLegacyModule")); } catch { }
                }

                // 3) Last resort: load the interop assembly straight from the BepInEx folder.
                if (asm == null)
                {
                    try
                    {
                        string root = null;
                        try { root = BepInEx.Paths.BepInExRootPath; } catch { }
                        if (!string.IsNullOrEmpty(root))
                        {
                            string path = System.IO.Path.Combine(root, "interop", "UnityEngine.InputLegacyModule.dll");
                            if (System.IO.File.Exists(path)) asm = Assembly.LoadFrom(path);
                        }
                    }
                    catch { }
                }

                if (asm != null)
                {
                    var t = asm.GetType("UnityEngine.Input", false);
                    if (t != null)
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name != "GetKeyDown") continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1) continue;
                            // Compare by type name to avoid a hard dependency on where KeyCode lives.
                            if (ps[0].ParameterType.FullName == "UnityEngine.KeyCode") { _getKeyDown = m; _argIsEnum = true; break; }
                            if (ps[0].ParameterType == typeof(int)) { _getKeyDown = m; _argIsEnum = false; break; }
                        }
                    }
                }

                _useWin32 = _getKeyDown == null;
            }
            catch
            {
                _useWin32 = true;
                _getKeyDown = null;
            }

            if (_useWin32)
                ModLog.Info("Keyboard input: legacy input module not present in this game build, using Win32 GetAsyncKeyState polling.");
            else
                ModLog.Info("Keyboard input: UnityEngine.Input (legacy module) active.");
        }

        // Maps the Unity KeyCode values used by this mod to Win32 virtual key codes.
        private static int ToVirtualKey(KeyCode key)
        {
            int k = (int)key;

            // F1..F15 -> VK_F1..VK_F15 (0x70..0x7E)
            if (k >= (int)KeyCode.F1 && k <= (int)KeyCode.F15)
                return 0x70 + (k - (int)KeyCode.F1);

            // Alpha0..Alpha9 -> 0x30..0x39 (same numeric order)
            if (k >= (int)KeyCode.Alpha0 && k <= (int)KeyCode.Alpha9)
                return 0x30 + (k - (int)KeyCode.Alpha0);

            // A..Z -> 0x41..0x5A
            if (k >= (int)KeyCode.A && k <= (int)KeyCode.Z)
                return 0x41 + (k - (int)KeyCode.A);

            // Keypad0..Keypad9 -> VK_NUMPAD0..9 (0x60..0x69)
            if (k >= (int)KeyCode.Keypad0 && k <= (int)KeyCode.Keypad9)
                return 0x60 + (k - (int)KeyCode.Keypad0);

            switch (key)
            {
                case KeyCode.KeypadPeriod: return 0x6E; // VK_DECIMAL
                case KeyCode.Escape: return 0x1B;       // VK_ESCAPE
                case KeyCode.Space: return 0x20;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Return: return 0x0D;
                case KeyCode.Backspace: return 0x08;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.LeftShift: return 0xA0;
                case KeyCode.RightShift: return 0xA1;
                case KeyCode.LeftControl: return 0xA2;
                case KeyCode.RightControl: return 0xA3;
                case KeyCode.LeftAlt: return 0xA4;
                case KeyCode.RightAlt: return 0xA5;
            }
            return 0;
        }
    }
}
