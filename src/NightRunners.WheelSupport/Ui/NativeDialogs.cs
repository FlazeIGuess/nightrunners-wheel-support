#pragma warning disable CA1416
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace NightRunners.WheelSupport.Ui
{
    // Native Win32 Open/Save file dialogs using comdlg32.dll.
    // Executed on a background STA thread so the Unity main thread and physics
    // never block or stutter while the user is choosing a file in Explorer.
    internal static class NativeDialogs
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class OpenFileName
        {
            public int structSize = Marshal.SizeOf(typeof(OpenFileName));
            public IntPtr hwndOwner = IntPtr.Zero;
            public IntPtr hInstance = IntPtr.Zero;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public string file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData = IntPtr.Zero;
            public IntPtr fnHook = IntPtr.Zero;
            public string templateName;
            public IntPtr pvReserved = IntPtr.Zero;
            public int dwReserved = 0;
            public int flagsEx = 0;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetSaveFileName([In, Out] OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private const int OFN_EXPLORER = 0x00080000;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;

        public static void OpenFileAsync(string title, string initialDir, Action<string> onFileSelected)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var ofn = new OpenFileName();
                    ofn.structSize = Marshal.SizeOf(typeof(OpenFileName));
                    try { ofn.hwndOwner = GetActiveWindow(); } catch { ofn.hwndOwner = IntPtr.Zero; }
                    ofn.filter = "NightRunners Config (*.json)\0*.json\0All Files (*.*)\0*.*\0\0";
                    var buf = new char[1024];
                    ofn.file = new string(buf);
                    ofn.maxFile = buf.Length;
                    ofn.title = title ?? "Select Wheel Profile JSON";
                    ofn.initialDir = string.IsNullOrEmpty(initialDir) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : initialDir;
                    ofn.flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;

                    if (GetOpenFileName(ofn))
                    {
                        string path = ofn.file;
                        int nullIdx = path.IndexOf('\0');
                        if (nullIdx >= 0) path = path.Substring(0, nullIdx);
                        path = path.Trim();
                        if (!string.IsNullOrEmpty(path))
                        {
                            onFileSelected?.Invoke(path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"NativeDialogs.OpenFileAsync error: {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        public static void SaveFileAsync(string title, string defaultFileName, string initialDir, Action<string> onFileSelected)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var ofn = new OpenFileName();
                    ofn.structSize = Marshal.SizeOf(typeof(OpenFileName));
                    try { ofn.hwndOwner = GetActiveWindow(); } catch { ofn.hwndOwner = IntPtr.Zero; }
                    ofn.filter = "NightRunners Config (*.json)\0*.json\0All Files (*.*)\0*.*\0\0";
                    string cleanDefault = defaultFileName ?? "profile.json";
                    if (!cleanDefault.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        cleanDefault += ".json";
                    var buf = new char[1024];
                    cleanDefault.CopyTo(0, buf, 0, Math.Min(cleanDefault.Length, buf.Length - 1));
                    ofn.file = new string(buf);
                    ofn.maxFile = buf.Length;
                    ofn.title = title ?? "Export Wheel Profile JSON";
                    ofn.initialDir = string.IsNullOrEmpty(initialDir) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : initialDir;
                    ofn.defExt = "json";
                    ofn.flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;

                    if (GetSaveFileName(ofn))
                    {
                        string path = ofn.file;
                        int nullIdx = path.IndexOf('\0');
                        if (nullIdx >= 0) path = path.Substring(0, nullIdx);
                        path = path.Trim();
                        if (!string.IsNullOrEmpty(path))
                        {
                            onFileSelected?.Invoke(path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Warn($"NativeDialogs.SaveFileAsync error: {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
    }
}
#pragma warning restore CA1416
