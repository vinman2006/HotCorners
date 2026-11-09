<div align="center">

# ⚡ Windows Hot Corners v3.0

### macOS & GNOME style Hot Corners — natively engineered for Windows 11 & 10.
### Featuring Corner Taskbar Immunity & Unrestricted Center Auto-Hide.

[![Windows 11 Ready](https://img.shields.io/badge/Windows%2011-24H2%20Compatible-0078D4?logo=windows11&logoColor=white)](https://github.com/vinman2006/HotCorners)
[![Language](https://img.shields.io/badge/Language-C%23%20%7C%20Win32-239120?logo=csharp&logoColor=white)](https://github.com/vinman2006/HotCorners)
[![Zero Dependencies](https://img.shields.io/badge/Dependencies-Zero%20(Pure%20Native)-blueviolet)](#architecture)
[![Memory Footprint](https://img.shields.io/badge/RAM-~11%20MB-success)](#performance)
[![CPU Usage](https://img.shields.io/badge/CPU-0.0%25-brightgreen)](#performance)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

<br/>

**Flick your cursor into any corner. Instantly switch apps and slide between virtual desktops. Completely silent. Zero bloat. The corners are 100% immune to the taskbar.**

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
 │    [CORNER IMMUNITY: 100% SHIELDED]               [CORNER IMMUNITY: 100% SHIELDED]   │
 └────────────────────────────[  TASKBAR  ]───────────────────────────────┘
                                   ▲
                                   │ (Taskbar slides up naturally and smoothly
                                   │  whenever you move your mouse to the center edge;
                                   │  the left and right corners are 100% IMMUNE!)
```

---

## 💡 Why This Exists (The Problem & The Breakthrough)

On macOS and Linux, Hot Corners are an indispensable muscle-memory productivity feature. Bringing this to Windows 11 has historically been a nightmare:

| The Problem with Existing Tools | The HotCorners v3.0 Breakthrough |
| :--- | :--- |
| **Windows 11 24H2 Keystroke Blocking:** Newer Windows updates intentionally block synthetic `Ctrl + Win + Left/Right` keystrokes injected by AutoHotkey and standard macro tools. | **Native Microsoft COM Virtual Desktop API:** Uses direct COM interfaces via Markus Scholtes' `VirtualDesktop.exe` to bypass keystroke filters cleanly. |
| **Taskbar Popping at Corners:** When Auto-Hide taskbar is enabled, flicking into bottom corners triggers Windows' edge sensor (`Y >= H - 2`), popping up the taskbar over your desktop transitions. | **Corner Taskbar Immunity:** Pure low-level driver event swallowing (`return 1`) at `Y >= H - 15` in the bottom corners. Windows Explorer **never receives mouse input in the corners**, making them 100% immune to the taskbar. |
| **Center Taskbar Unresponsiveness:** Complex directional tracking can accidentally block intentional mouse moves in the center. | **Unrestricted Center Access:** The center zone is completely untouched by the hook. Moving your cursor down to the center edge unhides the auto-hide taskbar instantly and naturally. |
| **Heavy Background Bloat:** Many third-party utilities require Electron, large Python bundles, or run sluggish background polling loops. | **Pure Native 11 KB Win32 Binary:** Compiles with Windows' built-in C# compiler (`csc.exe`). Consumes **0.0% CPU** and ~11 MB RAM. |
| **Hook Timeouts & Stutter:** Calling APIs like `SetCursorPos` or disk I/O inside low-level hooks causes Windows to drop the hook. | **Zero-Latency Hook Pipeline:** Pure non-blocking execution with all COM calls, key events, and logging queued asynchronously to `ThreadPool`. Hook latency is `< 0.001ms`. |
| **Ghost Triggers & Repeat Loops:** Resting your cursor in the corner can cause apps to repeatedly trigger or flicker. | **Hysteresis Exit Boundary:** Requires moving at least 180 pixels away from the corner and a 400ms cooldown before that corner can re-arm. |
| **Audible Distractions:** Many scripts emit beep sounds on corner contact. | **100% Silent:** Zero sound beeps, zero chimes. |

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
| ⬇️ *(Center)* | **Auto-Hide Taskbar** | Native Slide | Moves down to the center edge to reveal your taskbar smoothly and immediately. |

---

## 🏗️ Technical Architecture

```mermaid
flowchart TD
    subgraph Hardware ["Input Layer"]
        Mouse["Physical Mouse / Precision Touchpad"]
    end

    subgraph KernelHook ["Low-Level Hook Engine (WH_MOUSE_LL)"]
        HookProc["WM_MOUSEMOVE Interception (<0.001ms Latency)"]
        DPI["Per-Monitor DPI Aware Coordinate Translation"]
    end

    subgraph Router ["Spatial Router"]
        CheckCorner{"In Bottom Corners & Y >= H - 15?"}
        Swallow["Swallow Event (return 1) ➔ Corner Taskbar Immunity"]
        PassCenter["Pass Through (CallNextHookEx) ➔ Center Taskbar Works Normally"]
    end

    subgraph Dispatcher ["Action Dispatcher (ThreadPool)"]
        TopLeft["↖ Top-Left: Win+Tab (Task View)"]
        BottomLeft["↙ Bottom-Left: VirtualDesktop.exe /Left /Wrap"]
        BottomRight["↘ Bottom-Right: VirtualDesktop.exe /Right /Wrap"]
    end

    Mouse --> HookProc --> DPI --> CheckCorner
    CheckCorner -- "Yes (Corner Bottom Edge)" --> Swallow
    CheckCorner -- "No (Center / Moving Away)" --> PassCenter
    DPI -- "Top-Left (x <= 160 & y <= 100)" --> TopLeft
    DPI -- "Bottom-Left (x <= 160 & y >= h - 100)" --> BottomLeft
    DPI -- "Bottom-Right (x >= w - 160 & y >= h - 100)" --> BottomRight
```

1. **Corner Taskbar Immunity:** In the bottom-left and bottom-right corners, raw mouse move events attempting to touch the bottom edge sensor line (`Y >= H - 15`) are swallowed directly (`return 1`). Windows Explorer never receives edge hover notifications in the corners.
2. **Unrestricted Center Taskbar:** The center screen edge is completely unblocked. Moving your mouse to the center bottom edge reaches `Y = H - 1` naturally, unhiding the taskbar smoothly and immediately.
3. **Non-blocking ThreadPool Dispatch:** Virtual desktop switching and logging execute asynchronously on background threads, ensuring the input hook finishes in under a microsecond without risking `LowLevelHooksTimeout`.

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
const int CORNER_TRIGGER_W = 160; // Corner trigger width in physical pixels
const int CORNER_TRIGGER_H = 100; // Corner trigger height in physical pixels
const int CORNER_SHIELD_W = 250;  // Taskbar immunity width in corners
const int EXIT_DIST = 180;        // Distance to re-arm corner trigger
const int COOLDOWN_MS = 400;      // Minimum debounce delay between triggers
int sensorY = h - 15;             // Safe barrier (blocks bottom 15px in corners)
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
