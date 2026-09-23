using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace NightRunners.WheelSupport
{
    // Central logger. Verbose messages are throttled so the log does not flood on per-frame output.
    public static class ModLog
    {
        private static ManualLogSource _log;
        private static bool _verbose;
        private static readonly Dictionary<string, float> _lastThrottled = new Dictionary<string, float>();

        public static void Init(ManualLogSource log, bool verbose)
        {
            _log = log;
            _verbose = verbose;
        }

        public static void SetVerbose(bool v) => _verbose = v;
        public static bool Verbose => _verbose;

        public static void Info(string msg) => _log?.LogInfo(msg);
        public static void Warn(string msg) => _log?.LogWarning(msg);
        public static void Error(string msg) => _log?.LogError(msg);

        public static void Debug(string msg)
        {
            if (_verbose) _log?.LogInfo(msg);
        }

        // Emits a message for the given key at most every intervalSeconds (verbose mode only).
        // now = UnityEngine.Time.realtimeSinceStartup (main thread).
        public static void DebugThrottled(string key, float now, float intervalSeconds, string msg)
        {
            if (!_verbose) return;
            if (_lastThrottled.TryGetValue(key, out var last) && now - last < intervalSeconds) return;
            _lastThrottled[key] = now;
            _log?.LogInfo(msg);
        }

        // Verbose-independent throttled warning, so a recurring per-frame error surfaces at least
        // occasionally even with VerboseLogging off (which is the default).
        public static void WarnThrottled(string key, float now, float intervalSeconds, string msg)
        {
            string k = "W:" + key;
            if (_lastThrottled.TryGetValue(k, out var last) && now - last < intervalSeconds) return;
            _lastThrottled[k] = now;
            _log?.LogWarning(msg);
        }
    }
}
