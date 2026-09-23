using System;

namespace NightRunners.WheelSupport.Game
{
    // Snapshot of the player car state for the FFB computation (read on the main thread).
    public struct CarState
    {
        public bool Valid;
        public float Speed;
        public float OrgMaxSpeed;
        public float EngineRpm;
        public float MinRpm;
        public float MaxRpm;
        public float OgMaxRpm;
        public bool Redline;
        public float SteerAngle;
        public float VelocityAngle;
        public float FrontSlip;
        public float RearSlip;
        public bool Drifting;
        public float DriftAngle;
        public bool NotGrounded;
        public bool IsReset;
        public bool PlayerHasCrashed;
    }

    // Finds and caches the player car and reads its state.
    public static class PlayerCar
    {
        private static RCC_CarControllerV3 _cached;
        private static float _lastScan = -999f;
        private const float ScanInterval = 0.5f; // without a car found, scan at most every 0.5 s

        public static RCC_CarControllerV3 Get()
        {
            // prefer the cached instance while it is still valid (cheap every frame)
            try
            {
                if (_cached != null && _cached.Pointer != IntPtr.Zero)
                    return _cached;
            }
            catch { _cached = null; }

            // Throttle the search: in the menu / loading screen there is no player car, so do not
            // touch FindObjectsOfType / SceneManager every frame (avoids stutter).
            float now;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { now = 0f; }
            if (now - _lastScan < ScanInterval) return null;
            _lastScan = now;

            // 1) canonical: RCC_SceneManager.Instance.activePlayerVehicle
            try
            {
                var sm = RCC_SceneManager.Instance;
                if (sm != null)
                {
                    var v = sm.activePlayerVehicle;
                    if (v != null) { _cached = v; return v; }
                }
            }
            catch (Exception e) { ModLog.Debug($"SceneManager access failed: {e.Message}"); }

            // 2) fallback: scan all RCC cars; the player is controllable and not AI-driven
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<RCC_CarControllerV3>();
                if (all != null)
                {
                    foreach (var c in all)
                    {
                        try
                        {
                            if (c != null && c.canControl && !c.externalController)
                            {
                                _cached = c;
                                return c;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e) { ModLog.Debug($"FindObjectsOfType failed: {e.Message}"); }

            return null;
        }

        public static CarState Read()
        {
            var s = new CarState();
            var car = Get();
            if (car == null) return s;
            try
            {
                s.Speed = car.speed;
                s.OrgMaxSpeed = car.orgMaxSpeed;
                s.EngineRpm = car.engineRPM;
                s.MinRpm = car.minEngineRPM;
                s.MaxRpm = car.maxEngineRPM;
                s.OgMaxRpm = car.ogMaxRPM;
                s.Redline = car.redline;
                s.SteerAngle = car.steerAngle;
                s.VelocityAngle = car.velocityAngle;
                s.FrontSlip = car.frontSlip;
                s.RearSlip = car.rearSlip;
                s.Drifting = car.driftingNow;
                s.DriftAngle = car.driftAngle;
                s.NotGrounded = car.carIsNotGrounded;
                s.IsReset = car.isReset;
                s.PlayerHasCrashed = car.playerhasCrashed;
                s.Valid = true;
            }
            catch (Exception e)
            {
                ModLog.Debug($"Reading car state failed: {e.Message}");
                s.Valid = false;
            }
            return s;
        }

        public static void Invalidate() => _cached = null;
    }
}
