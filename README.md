<div align="center">

# ⚡ Windows Hot Corners v2.1

### macOS & GNOME style Hot Corners — natively engineered for Windows 11 & 10.
### Featuring the Zero-Taskbar Corner Shield & Up-to-Down Sensor Gate.

[![Windows 11 Ready](https://img.shields.io/badge/Windows%2011-24H2%20Compatible-0078D4?logo=windows11&logoColor=white)](https://github.com/vinman2006/HotCorners)
[![Language](https://img.shields.io/badge/Language-C%23%20%7C%20Win32-239120?logo=csharp&logoColor=white)](https://github.com/vinman2006/HotCorners)
[![Zero Dependencies](https://img.shields.io/badge/Dependencies-Zero%20(Pure%20Native)-blueviolet)](#architecture)
[![Memory Footprint](https://img.shields.io/badge/RAM-~11%20MB-success)](#performance)
[![CPU Usage](https://img.shields.io/badge/CPU-0.0%25-brightgreen)](#performance)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

<br/>

**Flick your cursor into any corner. Instantly switch apps and slide between virtual desktops. Completely silent. Zero bloat. Never accidentally reveals the taskbar in the corners.**

</div>

---

## 🖥️ Visual Overview: How It Works

```text
 ┌────────────────────────────────────────────────────────────────────────┐
 │ ↖️ TOP-LEFT CORNER                                                     │
 │    Flick here ➔  Task View (Win + Tab)                                 │
 │    Instantly exposes all open windows on your current workspace        │
 │                                                                        │
 │                                                                        │
 │                          YOUR WORKSPACE                                │
 │                                                                        │
 │                                                                        │
 │ ↙️ BOTTOM-LEFT CORNER                             BOTTOM-RIGHT CORNER ↘️│
 │    Flick here ➔  Slide Desktop Left               Flick here ➔  Slide Desktop Right  │
 │    [420px SHIELD WING: 100% SUPPRESSED]           [420px SHIELD WING: 100% SUPPRESSED]│
 └────────────────────────────[  TASKBAR  ]───────────────────────────────┘
                                   ▲
                                   │ (Taskbar ONLY slides up when moving
                                   │  from Up ➔ Down into the center edge;
                                   │  NEVER pops up in the corners or during
                                   │  horizontal swipes between them!)
```

---

## 💡 Why This Exists (The Problem & The Breakthrough)

On macOS and Linux, Hot Corners are an indispensable muscle-memory productivity feature. Bringing this to Windows 11 has historically been a nightmare:

| The Problem with Existing Tools | The HotCorners v2.1 Breakthrough |
| :--- | :--- |
| **Windows 11 24H2 Keystroke Blocking:** Newer Windows updates intentionally block synthetic `Ctrl + Win + Left/Right` keystrokes injected by AutoHotkey and standard macro tools. | **Native Microsoft COM Virtual Desktop API:** Uses direct COM interfaces via Markus Scholtes' `VirtualDesktop.exe` to bypass keystroke filters cleanly. |
| **Annoying Taskbar Popups on Diagonal Flicks:** When Auto-Hide taskbar is enabled, diagonal cursor flicks toward the bottom corners hit the bottom edge at `X = 100..300` before reaching the corner, waking the taskbar. | **Expanded 420px Shield Wings (`WH_MOUSE_LL`):** Full 420-pixel protective zones on the left and right flanks clamp the cursor at `H - 25` (1175 px). The bottom-edge sensor (`H - 2`) is never touched, preventing all accidental unhides during flicks. |
| **Taskbar Popping While Shifting Desktops:** Rapidly swiping between left and right corners sweeps across the bottom edge, triggering the taskbar repeatedly. | **Horizontal Sweep Suppression:** The directional gate suppresses horizontal bottom-edge sweeps (`lastY >= H - 40`), keeping the taskbar hidden during multi-desktop navigation. |
| **Center Taskbar Accessibility:** You still need to access your Start menu, search, and pinned apps smoothly. | **Up-to-Down Sensor Gate:** Moving your cursor intentionally from **Up ➔ Down** in the center zone instantly reveals the auto-hide taskbar without lag. |
| **Heavy Background Bloat:** Many third-party utilities require Electron, large Python bundles, or run sluggish background polling loops. | **Pure Native 11 KB Win32 Binary:** Compiles with Windows' built-in C# compiler (`csc.exe`). Consumes **0.0% CPU** and ~11 MB RAM. |
| **Ghost Triggers & Repeat Loops:** Resting your cursor in the corner can cause apps to repeatedly trigger or flicker. | **Hysteresis Exit Boundary:** Requires moving at least 160 pixels away from the corner and a 400ms cooldown before that corner can re-arm. |
| **Audible Distractions:** Many scripts emit beep sounds on corner contact. | **100% Silent:** Zero sound beeps, zero chimes. |
| **DPI Scaling Mismatches:** High-DPI screens (125%, 150%, 200%) calculate wrong corner hitboxes. | **Per-Monitor DPI Awareness:** Calls `SetProcessDPIAware()` to align physical screen metrics with hardware cursor coordinates 1:1. |

---

## ⚡ Quickstart (1-Click Installation)

You don't need Visual Studio, Node.js, Python, or AutoHotkey installed. Everything runs out of the box on stock Windows 11 & 10.

### 1. Download / Clone
```bash
git clone https://github.com/vinman2006/HotCorners.git
cd HotCorners
```
*(Or download the repository ZIP from GitHub and extract it).*

### 2. Install
Double-click **`install.bat`**.

That's it!
- It installs the engine to `%APPDATA%\WindowsHotCorners`.
- Configures permanent startup shortcuts in `shell:startup` and the Windows Registry.
- Launches the engine immediately in the background without needing a reboot.

---

## 🎯 Corner Controls & Key Actions

| Corner | Action | Shortcut Emulated | Experience |
| :---: | :---: | :---: | :--- |
| ↖️ | **Task View** | `Win + Tab` | Instant fluid overview of all running application windows. |
| ↙️ | **Previous Desktop** | `Ctrl + Win + Left` | Workspace slides seamlessly to the virtual desktop on the left. Taskbar stays 100% hidden. |
| ↘️ | **Next Desktop** | `Ctrl + Win + Right` | Workspace slides seamlessly to the virtual desktop on the right. Taskbar stays 100% hidden. |
| ⬇️ *(Center)* | **Auto-Hide Taskbar** | Native Slide | Moves down from top-to-bottom to reveal your taskbar without getting in the way of corners. |

---

## 🏗️ Technical Architecture

```mermaid
flowchart TD
    subgraph Hardware ["Input Layer"]
        Mouse["Physical Mouse / Precision Touchpad"]
    end

    subgraph KernelHook ["Low-Level Hook Engine (WH_MOUSE_LL)"]
        HookProc["WM_MOUSEMOVE Interception (<0.005ms Latency)"]
        DPI["Per-Monitor DPI Aware Coordinate Translation"]
    end

    subgraph CornerShield ["Zero-Taskbar Corner Shield & Direction Gate"]
        CheckWings{"In Corner Wings (X <= 420 or X >= W - 420)?"}
        ClampWing["Clamp to H - 25 & Swallow Event ➔ Taskbar Never Wakes"]
        CheckCenter{"In Center Zone: Moving UP ➔ DOWN?"}
        AllowCenter["Pass Through (CallNextHookEx) ➔ Taskbar Slides Up"]
        ClampSweep["Clamp to H - 25 (Horizontal Sweep Suppressed)"]
    end

    subgraph Dispatcher ["Action Dispatcher (ThreadPool)"]
        TopLeft["↖ Top-Left: Win+Tab (Task View)"]
        BottomLeft["↙ Bottom-Left: VirtualDesktop.exe /Left /Wrap"]
        BottomRight["↘ Bottom-Right: VirtualDesktop.exe /Right /Wrap"]
    end

    Mouse --> HookProc --> DPI --> CheckWings
    CheckWings -- "Yes (Within 420px Wings)" --> ClampWing
    CheckWings -- "No (Center Zone)" --> CheckCenter
    CheckCenter -- "Yes (dy > 0 from above)" --> AllowCenter
    CheckCenter -- "No (Sweeping along bottom)" --> ClampSweep
    DPI -- "Top-Left (x <= 120 & y <= 100)" --> TopLeft
    DPI -- "Bottom-Left (x <= 120 & y >= h - 100)" --> BottomLeft
    DPI -- "Bottom-Right (x >= w - 120 & y >= h - 100)" --> BottomRight
```

1. **Low-Level Hook Engine (`WH_MOUSE_LL`):** Runs an event-driven Win32 hook at the OS driver boundary. Requires **0.0% CPU** (no polling loops).
2. **Zero-Taskbar Corner Wings:** Enforces a 420-pixel wide buffer on both corners, clamping coordinates to `H - 25` (1175 px). Windows 11's taskbar sensor (`H - 2` / 1198 px) is never reached.
3. **Directional Sensor Gate:** Moving horizontally along the bottom edge is clamped so rapid shifts between corners never trigger the taskbar. An intentional downward motion (`dy > 0` originating from `Y < H - 40`) in the center zone unhides the taskbar naturally.
4. **Desktop Station Attachment:** Spawns a dedicated worker thread attached to `WinSta0\Default`, guaranteeing real-time cursor tracking across user account switches and lock screens.
5. **Non-blocking ThreadPool Workers:** Virtual desktop COM calls and keystroke injections run asynchronously on background threads so cursor movement never stalls or drops frames.

---

## 🛠️ Recompiling from Source

Want to audit or modify the code? You can recompile [`HotCorners.cs`](HotCorners.cs) in under 2 seconds using the built-in Windows C# compiler—no SDK or Visual Studio required:

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /target:winexe /optimize+ /out:'HotCorners.exe' 'HotCorners.cs'
```

---

## ⚙️ Customization

Open [`HotCorners.cs`](HotCorners.cs) in any text editor to adjust behavior:

```csharp
const int CORNER_TRIGGER_W = 120; // Corner trigger width in physical pixels
const int CORNER_TRIGGER_H = 100; // Corner trigger height in physical pixels
const int SHIELD_WING_W = 420;    // Width of corner shield wings
const int EXIT_DIST = 160;        // Distance to re-arm corner trigger
const int COOLDOWN_MS = 400;      // Minimum debounce delay between triggers
int safeY = h - 25;                // Barrier 25px above bottom edge (taskbar sensor is at h - 2)
```

---

## 🗑️ Uninstallation

Double-click **`uninstall.bat`**.
- Stops the running background process.
- Removes the `shell:startup` shortcut.
- Cleans up the registry run key.
- Deletes installed files cleanly.

---

## 📄 License

This project is open-source and available under the [MIT License](LICENSE).
Feel free to fork, adapt, and build upon it!
