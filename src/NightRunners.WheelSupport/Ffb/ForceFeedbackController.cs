using System;
using NightRunners.WheelSupport.Config;
using NightRunners.WheelSupport.Game;
using NightRunners.WheelSupport.Input;

namespace NightRunners.WheelSupport.Ffb
{
    // Computes the (intentionally simple) force feedback from the vehicle state and routes it to
    // two DirectInput channels: ConstantForce (centering + crash yank) and Sine (speed/RPM
    // vibration + slip rumble + crash rattle).
    public sealed class ForceFeedbackController
    {
        private readonly WheelConfig _cfg;
        private readonly DirectInputWheel _wheel;

        private float _crashUntil;      // realtime until which the crash effect runs
        private float _crashStart;      // start time of the current crash
        private float _crashLevel;      // 0..1 peak strength of the current crash
        private float _lastCrashAt = -999f;
        private float _rearmAt;         // next time to re-arm the effects

        // damper state: previous raw wheel position + time, to derive wheel velocity
        private float _lastPos;
        private float _lastNow;
        private bool _haveLast;

        public ForceFeedbackController(WheelConfig cfg, DirectInputWheel wheel)
        {
            _cfg = cfg;
            _wheel = wheel;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        private static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        // Called on a collision (from the collision patch). intensity ~ relativeVelocity.
        public void TriggerCrash(float intensity, float now)
        {
            if (!_cfg.FfbEnabled.Value) return;
            if (intensity < _cfg.CrashMinImpulse.Value) return;
            if (now - _lastCrashAt < _cfg.CrashCooldownMs.Value / 1000f) return;

            _lastCrashAt = now;
            _crashStart = now;
            _crashUntil = now + _cfg.CrashDurationMs.Value / 1000f;
            // Map intensity to 0..1: from CrashMinImpulse linearly to ~3x of it = full
            float span = Math.Max(0.001f, _cfg.CrashMinImpulse.Value * 2f);
            _crashLevel = Clamp01((intensity - _cfg.CrashMinImpulse.Value) / span);
            ModLog.Debug($"Crash FFB triggered: intensity={intensity:F1} level={_crashLevel:F2}");
        }

        // Called every frame by WheelRuntime. wheelSteer = currently injected steering position -1..1.
        public void Update(in CarState st, float wheelSteer, float now)
        {
            if (!_wheel.FfbAvailable) return;

            if (!_cfg.FfbEnabled.Value)
            {
                _wheel.SetConstantForce(0f);
                _wheel.SetVibration(0f, 10f);
                return;
            }

            float master = Clamp01(_cfg.FfbMasterGain.Value);

            // Gate: no forces while airborne or during reset
            bool gate = !st.Valid || st.NotGrounded || st.IsReset;

            // --- centering spring + damper (from the RAW physical wheel position) ---
            // Using the raw position (not the processed steer) means the force reflects the real
            // wheel angle with no dead zone and no early saturation from the input mapping.
            float pos = _wheel.SteerRaw;                 // -1..1 physical
            float mag = Math.Abs(pos);
            float sign = pos >= 0f ? 1f : -1f;

            // abs so the spring engages symmetrically in forward and reverse (car.speed is signed)
            float speedForCenter = st.Valid ? Math.Abs(st.Speed) : 0f;
            float minS = _cfg.CenteringMinSpeed.Value;
            float refS = Math.Max(minS + 0.01f, _cfg.CenteringSpeedRef.Value);
            float speedFactor = Clamp01((speedForCenter - minS) / (refS - minS));

            // spring: immediate off-center pretension (ramps in within ~6% so there is NO dead zone
            // and NO sudden breakaway) + a progressive curve toward the rim.
            float pretension = _cfg.CenterWeight.Value * Clamp01(mag / 0.06f);
            float curve = (float)Math.Pow(mag, Math.Max(0.1f, _cfg.SpringExponent.Value));
            float springMag = Clamp01(pretension + curve) * _cfg.CenteringGain.Value * speedFactor;
            float spring = -sign * springMag;            // opposes displacement -> centers

            // damper: opposes fast wheel movement; makes counter-steering possible and kills the
            // snappy feel. Scales with speed (wheel stays light at a standstill).
            float dt = _haveLast ? (now - _lastNow) : 0f;
            if (dt < 0.001f || dt > 0.1f) dt = 0.016f;
            float vel = _haveLast ? (pos - _lastPos) / dt : 0f;
            _lastPos = pos; _lastNow = now; _haveLast = true;
            float dampScale = 0.2f + 0.8f * speedFactor;
            float damper = -_cfg.DampingGain.Value * Clamp(vel * 0.15f, -1f, 1f) * dampScale;

            // combine, cap the centering part so the wheel never locks up
            float centerForce = spring + damper;
            float cap = _cfg.MaxForce.Value;
            if (centerForce > cap) centerForce = cap; else if (centerForce < -cap) centerForce = -cap;

            // --- crash contribution (decaying) ---
            float crashEnv = 0f;
            if (now < _crashUntil)
            {
                float dur = Math.Max(0.001f, _cfg.CrashDurationMs.Value / 1000f);
                float t = (now - _crashStart) / dur;      // 0..1
                crashEnv = _crashLevel * (1f - Clamp01(t)); // linear decay
            }
            // Crash yank: a short jolt pulling in one direction (tie it to the wheel sign)
            float crashDir = pos >= 0f ? -1f : 1f;
            float crashYank = crashEnv * _cfg.CrashGain.Value * crashDir;

            float constant = gate ? 0f : (centerForce + crashYank);
            if (_cfg.InvertFfb.Value) constant = -constant;
            constant *= master;
            _wheel.SetConstantForce(constant);

            // --- vibration (sine): speed, RPM redline, slip, crash rattle ---
            float speedNorm = 0f;
            if (st.Valid && st.OrgMaxSpeed > 1f) speedNorm = Clamp01(Math.Abs(st.Speed) / st.OrgMaxSpeed);

            float rpmNorm = 0f;
            if (st.Valid && st.MaxRpm > st.MinRpm)
                rpmNorm = Clamp01((st.EngineRpm - st.MinRpm) / (st.MaxRpm - st.MinRpm));

            float speedVib = _cfg.SpeedVibGain.Value * speedNorm;

            float rpmVib = 0f;
            if (st.Valid)
            {
                if (st.Redline) rpmVib = _cfg.RpmVibGain.Value;
                else rpmVib = _cfg.RpmVibGain.Value * Clamp01((rpmNorm - 0.85f) / 0.15f); // only near redline
            }

            float maxSlip = st.Valid ? Math.Max(Math.Abs(st.FrontSlip), Math.Abs(st.RearSlip)) : 0f;
            float slipRumble = 0f;
            float slipTh = _cfg.SlipThreshold.Value;
            if (maxSlip > slipTh)
                slipRumble = _cfg.SlipRumbleGain.Value * Clamp01((maxSlip - slipTh) / Math.Max(0.05f, 1f - slipTh));
            if (st.Valid && st.Drifting) slipRumble = Math.Max(slipRumble, _cfg.SlipRumbleGain.Value * 0.6f);

            float crashRattle = crashEnv; // turn vibration up fully during a crash

            float vibAmp = gate ? 0f : Math.Max(Math.Max(speedVib, slipRumble), Math.Max(rpmVib, crashRattle));
            vibAmp = Clamp01(vibAmp) * master;

            // Frequency: faster near redline/speed; hard during a crash
            float freqDrive = Math.Max(speedNorm, rpmNorm);
            float hz = Lerp(_cfg.SpeedVibMinHz.Value, _cfg.SpeedVibMaxHz.Value, freqDrive);
            if (crashEnv > 0f) hz = Math.Max(hz, 55f);

            _wheel.SetVibration(vibAmp, hz);

            // Re-arm the effects every 20 s (duration safeguard)
            if (now >= _rearmAt)
            {
                _rearmAt = now + 20f;
                _wheel.RearmEffects();
            }

            ModLog.DebugThrottled("ffb", now, 0.5f,
                $"FFB pos={pos:+0.00;-0.00} spring={spring:+0.00;-0.00} damp={damper:+0.00;-0.00} center={centerForce:+0.00;-0.00} final={constant:+0.00;-0.00} spd={speedForCenter:F1} sf={speedFactor:F2} | vib={vibAmp:F2}@{hz:F0}Hz slip={maxSlip:F2} drift={st.Drifting} crash={crashEnv:F2}");
        }

        public void SilenceAll()
        {
            try { _wheel.SetConstantForce(0f); } catch { }
            try { _wheel.SetVibration(0f, 10f); } catch { }
        }
    }
}
