# HotCorners Debugging & Tuning Prompt (v3.3 Reference)

**ROLE & MISSION:** Senior Win32 / Windows 11 Shell Debugging Architect (HotCorners v3.2 / v3.3)

---

## 1. PROJECT STATUS & EMPIRICAL BASELINE (WORKING REFERENCE)
We maintain a standalone, 0% CPU native C# background daemon (`HotCorners.exe`, compiled with .NET Framework 4.8 `csc.exe`) for Windows 11 Build 26100 (24H2 / 23H2).
* **Repository:** [vinman2006/HotCorners](https://github.com/vinman2006/HotCorners)
* **Target Hardware & Display Environment:**
  - ASUS TUF Gaming A16 (144 Hz display, Precision Touchpad + 1000 Hz gaming mouse).
  - Physical Resolution: 1920 × 1200 (1536 × 960 logical at 125% DPI scaling), Single Primary Monitor.
  - Taskbar Configuration: Auto-hide enabled (`ABS_AUTOHIDE`), Center aligned, Show Desktop corner disabled (`TaskbarSd = 0`), Search Icon only (`SearchboxTaskbarMode = 1`), Badges/Flashing disabled (`TaskbarBadges = 0`, `ForegroundFlashCount = 0`).

### Verified Empirical Constants (DO NOT GUESS OR ALTER WITHOUT CAUSE)
* **`Shell_TrayWnd` Off-Screen Bounding Box (Hidden State on 1920×1200):**
  - $\text{Left} = 0, \quad \text{Top} = 1198, \quad \text{Right} = 1920, \quad \text{Bottom} = 1256$ (unhidden $\text{Top} = 1140$).
  - Exactly **2 physical pixels** ($Y \in [1198, 1200)$) sit on-screen as the physical hit-test wake sensor across $0 \le X \le 1920$.
* **Wake Condition:** `Shell_TrayWnd` wakes if and only if the kernel cursor coordinate `gptCursorAsync` reaches $Y \ge 1198$ ($H - 2$), or if `Shell_TrayWnd` receives foreground focus. Any coordinate $Y \le 1197$ is 100% immune to proximity wake.

### The 3 Hidden OS Traps Discovered in Research
1. **Trap 1 (`WH_MOUSE_LL` vs. `gptCursorAsync`):** Returning `(IntPtr)1` from `LowLevelMouseProc` stops `WM_MOUSEMOVE` delivery to user-mode message queues, but if the cursor is not clamped, `explorer!CTray::v_WndProc` still sees `GetCursorPos` / Raw Input at $Y \ge 1198$.
2. **Trap 2 (The Virtual Desktop Switch Banner Collision):** Switching virtual desktops in Windows 11 normally renders a DWM/XAML desktop name overlay banner ("Desktop 1") over the bottom-center region, triggering the taskbar sensor and assigning foreground focus to `Shell_TrayWnd` (which locks `Shell_TrayWnd` open at $\text{Top} = 1140$ for 5+ seconds even when the cursor is parked motionless at $(960, 500)$!). Passing `/Animation:0 /Quiet` to `VirtualDesktop.exe` completely eliminates this banner and keeps $\text{Top} = 1198$.
3. **Trap 3 (Diagonal Flick Geometry):** High-speed diagonal flicks from $(960, 600)$ toward $(0, 1200)$ slice the bottom edge at $X \approx 130\text{--}180\text{px}$. Therefore, narrow $120\text{px}$ shields fail; $260\text{px}$ wide spatial shield wings ($X \le 260$ and $X \ge 1660$) are required, leaving a generous $1400\text{px}$ unrestricted center bottom edge ($260 < X < 1660$).

---

## 2. THE 4-LAYER v3.2/v3.3 ARCHITECTURE & KNOWN CODE PITFALLS TO AUDIT
When debugging `HotCorners.cs`, verify that the following 4 layers remain intact AND check whether any of the 3 Known v3.2 Code Bugs below are causing your issue:

### The 4 Core Layers (Must Preserve)
* **Layer 1 — Switch Animation Suppression (`/Animation:0`):** `VirtualDesktop.exe` MUST be invoked with `/Animation:0 /Quiet /Left /Wrap` (or `/Right /Wrap`), `CreateNoWindow = true`, and `UseShellExecute = false`.
* **Layer 2 — Spatial Geometry (1920×1200 Physical via Per-Monitor DPI Aware V2):**
  - `CORNER_W = 60, CORNER_H = 45` (tight deliberate trigger hitboxes).
  - `CORNER_SHIELD_W = 260, CORNER_SHIELD_H = 30` (wings at $0 \le X \le 260$ and $1660 \le X \le 1920$; unrestricted center at $260 < X < 1660$).
  - `RESET_HYSTERESIS_PX = 75, TRIGGER_COOLDOWN_MS = 400`.
* **Layer 3 — Sensor Clamping at $Y = 1190$ ($H - 10$, 8px above $Y = 1198$ sensor):** Prevents the cursor from ever reaching $Y \ge 1198$ inside the $260\text{px}$ wings.
* **Layer 4 — Post-Switch `SetWindowPos` Enforcer:** Immediately after a desktop switch, checks `GetWindowRect(hTray)` and clamps `Shell_TrayWnd` back to $Y = \text{\_screenH} - 2$ ($1198$) with `SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING`.

### ⚠️ CRITICAL AUDIT: 3 Subtle Code Bugs Found in v3.2 Implementation
If you are experiencing cursor stuttering, frozen horizontal movement in the bottom corners, or occasional hook dropouts, inspect `HotCorners.cs` for these 3 defects:

#### Bug A — Unconditional Packet Swallowing Freezes Horizontal (X-Axis) Movement in the Bottom 30px ($Y \ge 1170$):
* **The Flaw in v3.2:** In Layer 3, returning `(IntPtr)1` for every `WM_MOUSEMOVE` whenever `(x <= 260 || x >= 1660) && y >= H - 30` swallows horizontal X movement as well as downward Y movement! If the user's cursor enters $(200, 1175)$, the cursor completely freezes on screen and cannot even travel left to reach the $X \le 60$ trigger hitbox.
* **The Fix:** Only return `(IntPtr)1` when `rawY > H - 10` ($1190$), and use `ClipCursor` ($\text{Bottom} = H - 9$) to clamp the vertical axis in the kernel while letting X movement ($0..260$) pass through `CallNextHookEx` smoothly.

#### Bug B — Calling `SetCursorPos(rawX, H - 10)` Inside `LowLevelMouseProc`:
* **The Flaw in v3.2:** Calling `SetCursorPos` from inside a `WH_MOUSE_LL` hook injects a synthetic mouse move (`LLMHF_INJECTED`) back into the input queue. During a continuous downward touchpad/mouse swipe, this creates a 60+ call/sec feedback loop against the hardware driver and risks triggering Windows' `LowLevelHooksTimeout` (which silently unhooks `HotCorners.exe`).
* **The Fix:** Replace `SetCursorPos` inside the hook with a pre-armed kernel `ClipCursor` fence ($\text{Left} = 0, \text{Top} = 0, \text{Right} = W, \text{Bottom} = H - 9$) when entering the corner wing, and release `ClipCursor(IntPtr.Zero)` immediately when $260 < X < W - 260$.

#### Bug C — Trigger Hitbox vs. Clamp Deadlock (`CORNER_H` vs. `SAFE_CLAMP_OFFSET`):
* **The Flaw:** If the cursor is clamped at $Y = 1190$ ($H - 10$), any bottom-corner trigger condition that requires $Y \ge H - 5$ (e.g., $Y \ge 1195$) can never be reached!
* **The Fix:** Ensure the bottom-corner trigger condition evaluates `Y >= _screenH - CORNER_H` ($Y \ge 1155$, which is well above the $Y = 1190$ clamp ceiling) and `X <= CORNER_W` ($X \le 60$).

---

## 3. DEBUGGING & TUNING TEMPLATE
When reporting or debugging an edge case, provide:
1. **Symptom / Issue Observed:** (e.g. cursor feels stuck when moving horizontally near bottom-left corner, corner trigger requires too hard a flick, taskbar flashes after waking from sleep, or `VirtualDesktop.exe` fails on a specific build).
2. **Exact Reproduction Steps:** Step-by-step cursor movement coordinates.
3. **Expected vs. Actual Behavior:**
4. **Diagnostic Output:** Output from `HotCorners.exe --verify`.

---

## 4. REQUIRED DELIVERABLES FROM THE AI
1. **Exact Root-Cause Pinpoint:** Identify the exact coordinate math, Win32 state transition, or hook interaction responsible for the symptom.
2. **Surgical Code Fix:** Prove mathematically that:
   - The bottom-left/right trigger hitboxes (60×45px, $Y \ge 1155$) are reachable when clamped at $Y \le 1190$.
   - The $260\text{px}$ shield wings never allow `gptCursorAsync.Y` to reach $Y \ge 1198$.
   - The $1400\text{px}$ center edge ($260 < X < 1660$) remains 100% unclipped and reaches $Y = 1199$ with zero delay.
3. **Complete Compilable `HotCorners.cs`:** Output the full, single-file C# source compilable via:
   `& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /target:winexe /optimize+ /out:'HotCorners.exe' 'HotCorners.cs'`
4. **Verification Test:** Provide a one-command test to verify the fix on 1920×1200 (125% DPI).

