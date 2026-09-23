# NIGHT-RUNNERS Wheel Support (v0.1.0-alpha)

A standalone BepInEx 6 plugin that adds steering wheel support, custom DirectInput force feedback, manual clutch simulation, and H-shifter support to **NIGHT-RUNNERS** (Private Alpha).

---

### Early Alpha Notice

This mod is currently in early alpha development (version `0.1.0-alpha`).
- Not all steering wheels, pedal sets, or shifters are supported out of the box.
- You may encounter bugs, strange vehicle physics, or unexpected handling quirks while driving.
- Please test and tune your settings carefully in the F9 menu before doing high-speed highway runs.

---

### Third-Party Software Disclaimer

This mod relies on **BepInEx**, an open-source Unity modding framework maintained by the BepInEx team.
- BepInEx is a third-party application and is not developed by, affiliated with, or endorsed by Planet Jem Software or the developers of NIGHT-RUNNERS.
- Use of BepInEx and this mod is at your own discretion.

---

## Requirements

1. **NIGHT-RUNNERS** (Private Alpha build)
2. **BepInEx 6 (Unity IL2CPP x64)**
   - Official BepInEx Download: [BepInEx Bleeding Edge Builds](https://builds.bepinex.dev/projects/bepinex_be) or [BepInEx Releases on GitHub](https://github.com/BepInEx/BepInEx/releases)
   - Make sure you download the **`BepInEx_UnityIL2CPP_x64`** package (version 6.0.0-be.680 or newer).

---

## Installing BepInEx 6 (Step-by-Step)

If your game does not already have BepInEx installed:

1. Download the latest **`BepInEx_UnityIL2CPP_x64_...zip`** from the link above.
2. Open your NIGHT-RUNNERS installation directory (where `NIGHT-RUNNERS PRIVATE ALPHA.exe` is located).
3. Extract all contents of the zip file directly into the game root folder.
4. Your game directory should now look like this:
   ```text
   NIGHT-RUNNERS/
     ├── BepInEx/
     ├── dotnet/
     ├── doorstop_config.ini
     ├── winhttp.dll
     └── NIGHT-RUNNERS PRIVATE ALPHA.exe
   ```
5. Launch NIGHT-RUNNERS once. BepInEx will run its first-time setup, generate necessary folders, and close.

---

## Installing the Wheel Support Mod

1. Go to the **Releases** tab of this repository and download **`NightRunners.WheelSupport-v0.1.0-alpha.zip`**.
2. Extract the folder into your game's `BepInEx/plugins/` directory:
   ```text
   NIGHT-RUNNERS/
     └── BepInEx/
           └── plugins/
                 └── NightRunners.WheelSupport/
                       ├── NightRunners.WheelSupport.dll
                       ├── SharpDX.dll
                       └── SharpDX.DirectInput.dll
   ```
3. Connect your steering wheel and pedals, then launch NIGHT-RUNNERS.

---

## In-Game Hotkeys

| Hotkey | Description |
|---|---|
| **F9** | Open / close Settings Menu (live axis tuning, FFB sliders, profiles) |
| **F10** | Open Calibration Wizard (guided setup or instant profile import) |
| **F8** | Toggle the top-right HUD watermark on or off |
| **ESC** | Close any active menu overlay |

---

## How to Configure Your Wheel (Step-by-Step)

1. **Before Launching:** Connect your wheel, pedals, and shifter to your PC. Ensure your hardware manufacturer software (e.g. Logitech G HUB, Fanatec Control Panel, Thrustmaster Control Panel) is active and your wheel is calibrated to your preferred rotation degrees (e.g. 900 or 540 degrees).
2. **In-Game:** Once loaded into the garage or road, press **F10** to open the Calibration Wizard.
3. **Follow the Steps:**
   - **Step 1 (Intro):** Release all controls (wheel centered, feet off pedals), then click **Start Manual Calibration**.
     *(Alternative: Click "Import Profile" to instantly load a pre-configured `.json` file).*
   - **Step 2 (Steering Left):** Turn your wheel all the way to the left, hold it there, and click **Detect**.
   - **Step 3 (Steering Right):** Turn your wheel all the way to the right, hold it there, and click **Detect**.
   - **Step 4 (Throttle):** Press the throttle pedal all the way down, hold it, and click **Detect throttle**.
   - **Step 5 (Brake):** Press the brake pedal all the way down, hold it, and click **Detect brake**.
   - **Step 6 (Clutch - Optional):** Press the clutch pedal down and click **Detect clutch**, or click **Skip clutch**.
   - **Step 7 (Handbrake - Optional):** Pull your handbrake or press your desired button, then click **Use Button**, or click **Skip**.
   - **Step 8 & 9 (Paddles - Optional):** Press your shift-up and shift-down paddle/button, then click **Use Button**, or click **Skip**.
   - **Step 10 (H-Shifter - Optional):** Put your physical gear lever into each slot (1st through 6th and Reverse) and click **Set to Button** for each gear. If you do not have an H-shifter, click **Skip / Clear Shifter**.
   - **Step 11 (Summary):** Review your assignments and click **Save & finish**.

---

## Full Settings Reference (F9 Menu)

Press **F9** in-game at any time to open the live settings menu. All sliders and buttons take effect immediately.

### 1. Axes & Inversion
- **Steer:** DirectInput axis mapped to steering. Use the `<` and `>` buttons to cycle or click `detect` to move your wheel.
- **Invert steering:** Reverses steering direction if turning left steers right.
- **Throttle / Brake / Clutch:** DirectInput axes assigned to each pedal.
- **Invert throttle / brake / clutch:** Inverts the pedal signal. Most pedals (Logitech, Fanatec) output raw values where released = maximum and pressed = zero, which requires inversion enabled.

### 2. Steering & Physics Tuning
- **Lock range (0.20 - 1.00):** Adjusts how sharp the steering is. Lower values make steering sharper/more direct (fewer wheel degrees needed for full steering angle). Default is `0.50`.
- **Linearity (0.30 - 3.00):** Controls sensitivity around center. A value of `1.00` is pure 1:1 linear response. Values above `1.00` (e.g. `1.30` - `1.60`) make the car calmer on straights while retaining full turn angle at extremes.
- **Center deadzone (0.00 - 0.50):** Deadzone in the center of the wheel to eliminate minor sensor jitter.
- **Pedal deadzone (0.00 - 0.50):** Deadzone at the beginning of pedal travel to prevent resting foot pressure from dragging brakes or throttle.

### 3. Force Feedback (FFB)
- **FFB enabled:** Toggles all force feedback on or off.
- **Invert FFB direction:** Reverses force feedback direction. If your wheel pulls into corners instead of resisting your turn, toggle this immediately.
- **Master gain (0.00 - 1.00):** Global multiplier for all force feedback strength.
- **Centering (0.00 - 1.00):** Dynamic self-centering force that increases with vehicle speed.
- **Center weight (0.00 - 0.60):** Static resistance that keeps the wheel firm even at zero speed.
- **Spring curve (0.50 - 3.00):** Exponent of the centering spring. `1.00` is linear; higher values keep the center soft and firm up force at higher lock.
- **Max force (0.10 - 1.00):** Ceiling cap on centering force to prevent harsh clipping.
- **Damping (0.00 - 1.00):** Smooths out rapid oscillations and prevents high-speed tank-slappers.
- **Speed vibration (0.00 - 1.00):** Subtle road texture vibration scaling with speed.
- **RPM shake (0.00 - 1.00):** Engine vibration delivered to the wheel rim based on motor RPM.
- **Slip rumble (0.00 - 1.00):** Tire slip vibration that shakes the wheel when drifting or losing traction.
- **Crash jolt (0.00 - 1.00):** Impulse force delivered to the wheel upon collision with walls or cars.

### 4. Transmission & Shifter
- **Require clutch pedal to shift:** When enabled, shifting into gear requires holding the clutch pedal down (realistic manual). When disabled, gears engage directly.
- **Keyboard shifter emulation:** When enabled, keys 1-6 select gears, R engages Reverse, and 0 or N selects Neutral.
- **Gear 1-6 & Reverse Buttons:** Button indices mapped to each physical gate on your H-shifter. Neutral is automatic when no gear button is held.

### 5. Config Profiles (.json)
- **Save as:** Type a name and click Save to store your current settings as a `.json` file in `BepInEx/config/com.flaze.nightrunners.wheelsupport.profiles/`.
- **Export to file...:** Opens a native Windows save dialog to export your current settings or any saved profile to your Desktop, Downloads, or any folder.
- **Import profile (choose file...):** Opens a native Windows open dialog to pick any `.json` profile from your PC and apply it instantly.
- **Open profiles folder:** Opens the local profile storage directory in Windows Explorer.

---

## Troubleshooting

- **Wheel pulls hard to one side instead of centering:** Open **F9** and toggle **invert FFB direction**.
- **Car is constantly accelerating or braking on its own:** Open **F9** and toggle **invert throttle** or **invert brake**.
- **Steering feels too sensitive or twitchy:** In **F9**, increase **Linearity** to `1.40` or higher, or raise **Lock range** to `0.70` - `1.00`.
- **Wheel is not detected:** Ensure your wheel software (Logitech G HUB, Fanatec, Thrustmaster) is running before launching the game, and that your wheel is plugged into a USB 2.0/3.0 port directly on your PC (avoid unpowered USB hubs).

---

## Building from Source

Requires the **.NET 6 SDK**.

```bash
git clone https://github.com/FlazeIGuess/nightrunners-wheel-support.git
cd nightrunners-wheel-support
./build.sh
```

Compiled binaries will be created in `dist/plugins/NightRunners.WheelSupport/`.
