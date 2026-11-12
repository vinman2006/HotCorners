/*
 * HotCorners.cs — Native Win32 Hot Corners & Ergonomic Shortcuts Daemon for Windows 11 & 10
 * Configuration:
 *   1. Upper-Left Corner (Strict 0..4px Touch) -> Windows Task View (Win + Tab) [On/Off & Mode Toggleable].
 *   2. GNOME / Linux-Style Scroll-to-Switch-Desktops inside Task View [On/Off Toggleable]:
 *      While Task View (Win + Tab) is open, Mouse Wheel / Precision Touchpad scroll switches Virtual Desktops
 *      seamlessly while keeping Task View visible.
 *   3. Independent Keyboard Shortcuts (Each individually On/Off toggleable & persisted in HKCU):
 *        - F1 -> Alt + Tab (atomic press & release quick switch)
 *        - F9 / F10 -> Virtual Desktop Left / Right (Ctrl + Win + Left / Right)
 *        - Volume Up / Down -> Right / Left Arrow
 *   4. Zero-CPU SetWinEventHook Self-Healing Guard for Windows 11 'Task Switching' (Alt+Tab) Popup.
 *   5. Bottom corners & bottom edge remain 100% untouched stock Windows native behavior.
 *
 * Compilation (.NET Framework 4.8 Built-in Compiler):
 *   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /win32icon:HotCorners.ico /out:HotCorners.exe HotCorners.cs
 */

using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HotCornersDaemon
{
    public enum CornerAction
    {
        TaskView = 0,       // Native 'Task View' (Win + Tab via Shell.Application.WindowSwitcher) [DEFAULT]
        WindowSwitcher = 1  // Native Alt+Tab 'Task Switching' popup (Ctrl + Alt + Tab)
    }

    internal static class Program
    {
        // =====================================================================
        // 1. CONFIGURATION & DEFAULTS (Persisted in HKCU\Software\WindowsHotCorners)
        // =====================================================================
        private const string REG_KEY_PATH = @"Software\WindowsHotCorners";
        private const string REG_VAL_ACTION = "TopLeftMode";
        private const string REG_VAL_TOPLEFT_ON = "TopLeftEnabled";
        private const string REG_VAL_SCROLL_ON = "TaskViewScrollEnabled";
        private const string REG_VAL_F1_ALTTAB = "ShortcutF1AltTab";
        private const string REG_VAL_F9F10_VD = "ShortcutF9F10Desktop";
        private const string REG_VAL_VOL_ARROWS = "ShortcutVolArrows";

        private static CornerAction _topLeftAction = CornerAction.TaskView;
        private static bool _enableTopLeft = true;
        private static bool _enableTaskViewScroll = true;
        private static bool _enableF1AltTab = true;
        private static bool _enableF9F10Desktop = true;
        private static bool _enableVolArrows = false;

        private const bool ENABLE_BOTTOM_LEFT = false;         // Disabled (Stock native Windows)
        private const bool ENABLE_BOTTOM_RIGHT = false;        // Disabled (Stock native Windows)

        private const int CORNER_W = 4;                        // Strict corner-touch hitbox width (0..4 px)
        private const int CORNER_H = 4;                        // Strict corner-touch hitbox height (0..4 px)
        private const int RESET_HYSTERESIS_PX = 80;            // Distance out of corner to re-arm (px)
        private const long TRIGGER_COOLDOWN_MS = 400;          // Cooldown between consecutive corner toggles (ms)

        private const int WHEEL_THRESHOLD = 120;               // 1 standard wheel notch (accumulates touchpad deltas)
        private const long SCROLL_SWITCH_COOLDOWN_MS = 150;    // Cooldown between desktop steps while scrolling (ms)
        private const long SCROLL_ACCUM_RESET_MS = 350;        // Reset partial touchpad accumulation after idle pause

        // =====================================================================
        // 2. WIN32 API DECLARATIONS
        // =====================================================================
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_SETTINGCHANGE = 0x001A;

        private const uint LLMHF_INJECTED = 0x00000001;
        private const uint LLKHF_INJECTED = 0x00000010;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private const ushort VK_TAB = 0x09;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LEFT = 0x25;
        private const ushort VK_RIGHT = 0x27;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const ushort VK_F1 = 0x70;
        private const ushort VK_F9 = 0x78;
        private const ushort VK_F10 = 0x79;
        private const ushort VK_LSHIFT = 0xA0;
        private const ushort VK_RSHIFT = 0xA1;
        private const ushort VK_LCONTROL = 0xA2;
        private const ushort VK_RCONTROL = 0xA3;
        private const ushort VK_LMENU = 0xA4;
        private const ushort VK_RMENU = 0xA5;
        private const ushort VK_VOLUME_DOWN = 0xAE;
        private const ushort VK_VOLUME_UP = 0xAF;

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint INPUT_KEYBOARD = 1;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int DWMWA_CLOAK = 14;

        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // =====================================================================
        // 3. DAEMON STATE
        // =====================================================================
        private static IntPtr _mouseHookHandle = IntPtr.Zero;
        private static LowLevelHookProc _mouseHookDelegate;

        private static IntPtr _keyboardHookHandle = IntPtr.Zero;
        private static LowLevelHookProc _keyboardHookDelegate;

        private static IntPtr _minimizeHook = IntPtr.Zero;
        private static IntPtr _foregroundHook = IntPtr.Zero;
        private static WinEventDelegate _winEventDelegate;

        private static int _screenW;
        private static int _screenH;

        private static bool _topLeftArmed = true;
        private static long _lastTriggerTick = 0;

        private static int _scrollAccum = 0;
        private static long _lastWheelTick = 0;
        private static long _lastScrollSwitchTick = 0;

        private static bool _f1Down = false;
        private static bool _f9Down = false;
        private static bool _f10Down = false;

        private static readonly Stopwatch _Clock = Stopwatch.StartNew();

        private static NotifyIcon _TrayIcon;
        private static IntPtr _customTrayIconHandle = IntPtr.Zero;
        private static MenuItem _menuTopLeftEnabled;
        private static MenuItem _menuTaskView;
        private static MenuItem _menuAltTabPopup;
        private static MenuItem _menuScrollDesktops;
        private static MenuItem _menuF1AltTab;
        private static MenuItem _menuF9F10Desktop;
        private static MenuItem _menuVolArrows;

        // =====================================================================
        // 4. ENTRY POINT & PERSISTENCE
        // =====================================================================
        [STAThread]
        private static void Main(string[] args)
        {
            EnablePhysicalDpiAwareness();
            UpdateScreenMetrics();
            LoadPreferences();

            if (args != null && args.Length > 0 &&
                (string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "/verify", StringComparison.OrdinalIgnoreCase)))
            {
                RunVerificationDiagnostics();
                return;
            }

            bool createdNew;
            using (Mutex singleInstanceMutex = new Mutex(true, @"Local\Win11HotCornersDaemon_TopLeftOnly", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                // Ensure any previously trapped modifier keys & minimized Task Switching window are healed on startup
                ReleaseStuckModifiers();
                RepairTaskSwitchingIfMinimized(false);

                // Install Low-Level Mouse Hook (Corner Touch + Task View Scroll Switcher)
                _mouseHookDelegate = MouseHookCallback;
                _keyboardHookDelegate = KeyboardHookCallback;
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    IntPtr hMod = GetModuleHandle(curModule.ModuleName);
                    _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, hMod, 0);
                }

                // Install Keyboard Hook if any keyboard shortcut toggle is active
                SyncKeyboardHook();

                // Install Zero-CPU WinEvent guard for Windows 11 'Task Switching' SW_SHOWMINIMIZED bug
                _winEventDelegate = OnWinEvent;
                _minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART,
                    IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
                _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);

                InitializeTrayIcon();

                Application.ApplicationExit += OnApplicationExit;
                Application.Run(new SystemEventListenerWindow());
            }
        }

        private static void EnablePhysicalDpiAwareness()
        {
            try
            {
                if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                {
                    SetProcessDPIAware();
                }
            }
            catch
            {
                SetProcessDPIAware();
            }
        }

        internal static void UpdateScreenMetrics()
        {
            _screenW = GetSystemMetrics(SM_CXSCREEN);
            _screenH = GetSystemMetrics(SM_CYSCREEN);
        }

        private static bool ReadBoolPref(RegistryKey key, string name, bool defaultVal)
        {
            object val = key.GetValue(name);
            return (val is int) ? ((int)val != 0) : defaultVal;
        }

        private static void LoadPreferences()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_KEY_PATH))
                {
                    if (key == null) return;

                    object actionVal = key.GetValue(REG_VAL_ACTION);
                    if (actionVal is int && Enum.IsDefined(typeof(CornerAction), (int)actionVal))
                    {
                        _topLeftAction = (CornerAction)(int)actionVal;
                    }

                    _enableTopLeft = ReadBoolPref(key, REG_VAL_TOPLEFT_ON, true);
                    _enableTaskViewScroll = ReadBoolPref(key, REG_VAL_SCROLL_ON, true);
                    _enableF1AltTab = ReadBoolPref(key, REG_VAL_F1_ALTTAB, true);
                    _enableF9F10Desktop = ReadBoolPref(key, REG_VAL_F9F10_VD, true);
                    _enableVolArrows = ReadBoolPref(key, REG_VAL_VOL_ARROWS, false);
                }
            }
            catch
            {
            }
        }

        private static void SaveIntPref(string name, int value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(REG_KEY_PATH))
                {
                    if (key != null)
                    {
                        key.SetValue(name, value, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
            }
        }

        private static void SyncKeyboardHook()
        {
            bool needHook = _enableF1AltTab || _enableF9F10Desktop || _enableVolArrows;
            if (needHook && _keyboardHookHandle == IntPtr.Zero)
            {
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    _keyboardHookHandle = SetWindowsHookEx(
                        WH_KEYBOARD_LL, _keyboardHookDelegate, GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            else if (!needHook && _keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
                _f1Down = false;
                _f9Down = false;
                _f10Down = false;
            }
        }

        // =====================================================================
        // 5. SELF-HEALING 'TASK SWITCHING' (ALT+TAB) OVERLAY GUARD
        // =====================================================================
        private static void OnWinEvent(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || idObject != 0)
            {
                return;
            }

            if (IsIslandWindowWithTitle(hwnd, "Task Switching") && IsIconic(hwnd))
            {
                bool keepVisibleIfActivating = (eventType == EVENT_SYSTEM_FOREGROUND);
                RepairTaskSwitchingHwnd(hwnd, keepVisibleIfActivating);
            }
        }

        private static bool IsIslandWindowWithTitle(IntPtr hwnd, string expectedTitle)
        {
            StringBuilder cls = new StringBuilder(64);
            if (GetClassName(hwnd, cls, cls.Capacity) == 0 ||
                !string.Equals(cls.ToString(), "XamlExplorerHostIslandWindow", StringComparison.Ordinal))
            {
                return false;
            }

            StringBuilder title = new StringBuilder(64);
            GetWindowText(hwnd, title, title.Capacity);
            return string.Equals(title.ToString(), expectedTitle, StringComparison.Ordinal);
        }

        internal static void RepairTaskSwitchingIfMinimized(bool keepVisible)
        {
            IntPtr hTaskSwitching = FindWindow("XamlExplorerHostIslandWindow", "Task Switching");
            if (hTaskSwitching != IntPtr.Zero && IsIconic(hTaskSwitching))
            {
                RepairTaskSwitchingHwnd(hTaskSwitching, keepVisible);
            }
        }

        private static void RepairTaskSwitchingHwnd(IntPtr hTaskSwitching, bool keepVisible)
        {
            try
            {
                if (!keepVisible)
                {
                    int cloak = 1;
                    DwmSetWindowAttribute(hTaskSwitching, DWMWA_CLOAK, ref cloak, sizeof(int));
                    ShowWindow(hTaskSwitching, SW_SHOWNOACTIVATE);
                    ShowWindow(hTaskSwitching, SW_HIDE);
                }
                else
                {
                    ShowWindow(hTaskSwitching, SW_SHOWNOACTIVATE);
                }
            }
            catch
            {
            }
        }

        private static bool IsTaskViewOpen()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && IsIslandWindowWithTitle(fg, "Task View"))
            {
                return true;
            }

            IntPtr hTV = FindWindow("XamlExplorerHostIslandWindow", "Task View");
            return hTV != IntPtr.Zero && IsWindowVisible(hTV);
        }

        // =====================================================================
        // 6. LOW-LEVEL MOUSE HOOK (Strict Corner Touch + Task View Scroll)
        // =====================================================================
        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();

                if (msg == WM_MOUSEMOVE && _enableTopLeft)
                {
                    MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));

                    if ((hookStruct.flags & LLMHF_INJECTED) != 0)
                    {
                        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
                    }

                    int rawX = hookStruct.pt.X;
                    int rawY = hookStruct.pt.Y;

                    int x = rawX < 0 ? 0 : (rawX >= _screenW ? _screenW - 1 : rawX);
                    int y = rawY < 0 ? 0 : (rawY >= _screenH ? _screenH - 1 : rawY);

                    if (!_topLeftArmed && (x > CORNER_W + RESET_HYSTERESIS_PX || y > CORNER_H + RESET_HYSTERESIS_PX))
                    {
                        _topLeftArmed = true;
                    }

                    // Strict Corner-Touch Trigger (X <= 4, Y <= 4)
                    if (x <= CORNER_W && y <= CORNER_H)
                    {
                        long nowMs = (_Clock.ElapsedTicks * 1000L) / Stopwatch.Frequency;
                        if (_topLeftArmed && (nowMs - _lastTriggerTick) >= TRIGGER_COOLDOWN_MS)
                        {
                            _topLeftArmed = false;
                            _lastTriggerTick = nowMs;
                            TriggerTopLeftAsync();
                        }
                    }
                }
                else if (_enableTaskViewScroll && (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL))
                {
                    MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if ((hookStruct.flags & LLMHF_INJECTED) == 0 && IsTaskViewOpen())
                    {
                        short rawDelta = unchecked((short)((uint)hookStruct.mouseData >> 16));
                        if (rawDelta != 0)
                        {
                            int stepDelta = (msg == WM_MOUSEWHEEL) ? -rawDelta : rawDelta;
                            long nowMs = (_Clock.ElapsedTicks * 1000L) / Stopwatch.Frequency;

                            if ((nowMs - _lastWheelTick) > SCROLL_ACCUM_RESET_MS ||
                                (_scrollAccum != 0 && Math.Sign(_scrollAccum) != Math.Sign(stepDelta)))
                            {
                                _scrollAccum = 0;
                            }

                            _lastWheelTick = nowMs;
                            _scrollAccum += stepDelta;

                            if (Math.Abs(_scrollAccum) >= WHEEL_THRESHOLD)
                            {
                                bool goRight = (_scrollAccum > 0);
                                _scrollAccum = 0;

                                if ((nowMs - _lastScrollSwitchTick) >= SCROLL_SWITCH_COOLDOWN_MS)
                                {
                                    _lastScrollSwitchTick = nowMs;
                                    SwitchVirtualDesktopAsync(goRight);
                                }
                            }
                        }

                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        // =====================================================================
        // 7. LOW-LEVEL KEYBOARD HOOK (F1->Alt+Tab, F9/F10->Desktop, Vol->Arrows)
        // =====================================================================
        private static bool IsKeyDown(ushort vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private static bool IsAnyModifierDown()
        {
            return IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL) ||
                   IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU) ||
                   IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_RSHIFT) ||
                   IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
        }

        private static bool HandleTriggerOnceKey(bool isKeyDown, bool isKeyUp, ref bool isDownFlag, Action onTrigger)
        {
            if (isKeyDown)
            {
                if (!isDownFlag)
                {
                    if (IsAnyModifierDown()) return false;
                    isDownFlag = true;
                    onTrigger();
                }
                return true;
            }
            if (isKeyUp && isDownFlag)
            {
                isDownFlag = false;
                return true;
            }
            return false;
        }

        private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT kbd = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((kbd.flags & LLKHF_INJECTED) != 0)
                {
                    return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
                }

                int msg = wParam.ToInt32();
                bool isKeyDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isKeyUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);
                uint vk = kbd.vkCode;

                // 1. F1 -> Quick Alt+Tab + Release
                if (_enableF1AltTab && vk == VK_F1)
                {
                    if (HandleTriggerOnceKey(isKeyDown, isKeyUp, ref _f1Down, TriggerQuickAltTabAsync))
                    {
                        return (IntPtr)1;
                    }
                }

                // 2. F9 / F10 -> Previous / Next Virtual Desktop
                if (_enableF9F10Desktop && vk == VK_F9)
                {
                    if (HandleTriggerOnceKey(isKeyDown, isKeyUp, ref _f9Down, () => SwitchVirtualDesktopAsync(false)))
                    {
                        return (IntPtr)1;
                    }
                }
                if (_enableF9F10Desktop && vk == VK_F10)
                {
                    if (HandleTriggerOnceKey(isKeyDown, isKeyUp, ref _f10Down, () => SwitchVirtualDesktopAsync(true)))
                    {
                        return (IntPtr)1;
                    }
                }

                // 3. Volume Down / Volume Up -> Left / Right Arrow (supports hold & repeat)
                if (_enableVolArrows && (vk == VK_VOLUME_DOWN || vk == VK_VOLUME_UP))
                {
                    ushort targetArrow = (vk == VK_VOLUME_UP) ? VK_RIGHT : VK_LEFT;
                    if (isKeyDown || isKeyUp)
                    {
                        INPUT[] inp = new INPUT[] { MakeKeyInput(targetArrow, isKeyDown, true) };
                        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // =====================================================================
        // 8. ASYNC ACTION & DESKTOP SWITCH DISPATCH
        // =====================================================================
        private static bool IsMouseButtonPressed()
        {
            return IsKeyDown(0x01) || IsKeyDown(0x02);
        }

        private static bool IsExclusiveFullscreenActive()
        {
            int state;
            if (SHQueryUserNotificationState(out state) == 0)
            {
                return state == QUNS_RUNNING_D3D_FULL_SCREEN;
            }
            return false;
        }

        private static INPUT MakeKeyInput(ushort vk, bool isDown, bool isExtended = false)
        {
            uint flags = (isDown ? 0u : KEYEVENTF_KEYUP) | (isExtended ? KEYEVENTF_EXTENDEDKEY : 0u);
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)MapVirtualKey(vk, 0),
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private static void TriggerQuickAltTabAsync()
        {
            ThreadPool.QueueUserWorkItem((state) =>
            {
                try
                {
                    INPUT[] seq = new INPUT[]
                    {
                        MakeKeyInput(VK_LMENU, true, false),
                        MakeKeyInput(VK_TAB, true, false),
                        MakeKeyInput(VK_TAB, false, false),
                        MakeKeyInput(VK_LMENU, false, false)
                    };
                    SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
                }
                catch
                {
                    ReleaseStuckModifiers();
                }
            });
        }

        private static void SwitchVirtualDesktopAsync(bool goRight)
        {
            ThreadPool.QueueUserWorkItem((state) =>
            {
                try
                {
                    ushort arrowKey = goRight ? VK_RIGHT : VK_LEFT;
                    INPUT[] seq = new INPUT[]
                    {
                        MakeKeyInput(VK_LCONTROL, true, false),
                        MakeKeyInput(VK_LWIN, true, false),
                        MakeKeyInput(arrowKey, true, true),
                        MakeKeyInput(arrowKey, false, true),
                        MakeKeyInput(VK_LWIN, false, false),
                        MakeKeyInput(VK_LCONTROL, false, false)
                    };
                    SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
                }
                catch
                {
                    ReleaseStuckModifiers();
                }
            });
        }

        private static void TriggerTopLeftAsync()
        {
            if (IsMouseButtonPressed() || IsExclusiveFullscreenActive())
            {
                return;
            }

            CornerAction action = _topLeftAction;
            ThreadPool.QueueUserWorkItem((state) =>
            {
                try
                {
                    if (action == CornerAction.TaskView)
                    {
                        Type shellType = Type.GetTypeFromProgID("Shell.Application");
                        if (shellType != null)
                        {
                            object shell = Activator.CreateInstance(shellType);
                            shellType.InvokeMember("WindowSwitcher", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                            Marshal.ReleaseComObject(shell);
                        }
                        else
                        {
                            INPUT[] seq = new INPUT[]
                            {
                                MakeKeyInput(VK_LWIN, true),
                                MakeKeyInput(VK_TAB, true),
                                MakeKeyInput(VK_TAB, false),
                                MakeKeyInput(VK_LWIN, false)
                            };
                            SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
                        }
                    }
                    else
                    {
                        RepairTaskSwitchingIfMinimized(false);
                        INPUT[] seq = new INPUT[]
                        {
                            MakeKeyInput(VK_LCONTROL, true),
                            MakeKeyInput(VK_LMENU, true),
                            MakeKeyInput(VK_TAB, true),
                            MakeKeyInput(VK_TAB, false),
                            MakeKeyInput(VK_LMENU, false),
                            MakeKeyInput(VK_LCONTROL, false)
                        };
                        SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
                    }
                }
                catch
                {
                    ReleaseStuckModifiers();
                }
            });
        }

        internal static void ReleaseStuckModifiers()
        {
            try
            {
                INPUT[] upSeq = new INPUT[]
                {
                    MakeKeyInput(VK_MENU, false),
                    MakeKeyInput(VK_LMENU, false),
                    MakeKeyInput(VK_RMENU, false),
                    MakeKeyInput(VK_LWIN, false),
                    MakeKeyInput(VK_RWIN, false),
                    MakeKeyInput(VK_CONTROL, false),
                    MakeKeyInput(VK_LCONTROL, false),
                    MakeKeyInput(VK_RCONTROL, false),
                    MakeKeyInput(VK_SHIFT, false),
                    MakeKeyInput(VK_LSHIFT, false),
                    MakeKeyInput(VK_RSHIFT, false),
                    MakeKeyInput(VK_TAB, false)
                };
                SendInput((uint)upSeq.Length, upSeq, Marshal.SizeOf(typeof(INPUT)));
            }
            catch
            {
            }
        }

        // =====================================================================
        // 9. SYSTEM TRAY ICON & INDEPENDENT ON/OFF MENU CONTROLS
        // =====================================================================
        private static void InitializeTrayIcon()
        {
            ContextMenu trayMenu = new ContextMenu();

            // Section 1: Hot Corners & Scroll Switching
            _menuTopLeftEnabled = new MenuItem("Hot Corner: Upper-Left Enabled", (s, e) =>
            {
                _enableTopLeft = !_enableTopLeft;
                SaveIntPref(REG_VAL_TOPLEFT_ON, _enableTopLeft ? 1 : 0);
                UpdateTrayMenu();
            });

            _menuTaskView = new MenuItem("   Upper-Left: Task View (Win + Tab) + Scroll Desktops", (s, e) =>
            {
                _topLeftAction = CornerAction.TaskView;
                _enableTopLeft = true;
                SaveIntPref(REG_VAL_ACTION, (int)_topLeftAction);
                SaveIntPref(REG_VAL_TOPLEFT_ON, 1);
                UpdateTrayMenu();
            });

            _menuAltTabPopup = new MenuItem("   Upper-Left: Window Switcher (Alt + Tab Popup)", (s, e) =>
            {
                _topLeftAction = CornerAction.WindowSwitcher;
                _enableTopLeft = true;
                SaveIntPref(REG_VAL_ACTION, (int)_topLeftAction);
                SaveIntPref(REG_VAL_TOPLEFT_ON, 1);
                UpdateTrayMenu();
            });

            _menuScrollDesktops = new MenuItem("Task View: Scroll Wheel Switches Desktops", (s, e) =>
            {
                _enableTaskViewScroll = !_enableTaskViewScroll;
                SaveIntPref(REG_VAL_SCROLL_ON, _enableTaskViewScroll ? 1 : 0);
                UpdateTrayMenu();
            });

            // Section 2: Separate Independent Keyboard Shortcuts (On/Off)
            _menuF1AltTab = new MenuItem("Shortcut: F1 -> Alt + Tab (Switch && Release)", (s, e) =>
            {
                _enableF1AltTab = !_enableF1AltTab;
                SaveIntPref(REG_VAL_F1_ALTTAB, _enableF1AltTab ? 1 : 0);
                SyncKeyboardHook();
                UpdateTrayMenu();
            });

            _menuF9F10Desktop = new MenuItem("Shortcut: F9 / F10 -> Switch Desktop (Left / Right)", (s, e) =>
            {
                _enableF9F10Desktop = !_enableF9F10Desktop;
                SaveIntPref(REG_VAL_F9F10_VD, _enableF9F10Desktop ? 1 : 0);
                SyncKeyboardHook();
                UpdateTrayMenu();
            });

            _menuVolArrows = new MenuItem("Shortcut: Volume Up / Down -> Right / Left Arrow", (s, e) =>
            {
                _enableVolArrows = !_enableVolArrows;
                SaveIntPref(REG_VAL_VOL_ARROWS, _enableVolArrows ? 1 : 0);
                SyncKeyboardHook();
                UpdateTrayMenu();
            });

            trayMenu.MenuItems.Add(_menuTopLeftEnabled);
            trayMenu.MenuItems.Add(_menuTaskView);
            trayMenu.MenuItems.Add(_menuAltTabPopup);
            trayMenu.MenuItems.Add(_menuScrollDesktops);
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add(_menuF1AltTab);
            trayMenu.MenuItems.Add(_menuF9F10Desktop);
            trayMenu.MenuItems.Add(_menuVolArrows);
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Repair Alt+Tab Popup && Release Stuck Keys", (s, e) =>
            {
                ReleaseStuckModifiers();
                RepairTaskSwitchingIfMinimized(false);
                MessageBox.Show("Alt+Tab 'Task Switching' state verified/repaired and modifier keys released.",
                    "Hot Corners", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Exit Hot Corners", (s, e) => Application.Exit());

            _TrayIcon = new NotifyIcon
            {
                Icon = LoadAppTrayIcon(),
                ContextMenu = trayMenu,
                Visible = true
            };

            UpdateTrayMenu();
        }

        private static Icon LoadAppTrayIcon()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                IntPtr[] smallIcons = new IntPtr[1];
                uint count = ExtractIconEx(exePath, 0, null, smallIcons, 1);
                if (count > 0 && smallIcons[0] != IntPtr.Zero)
                {
                    _customTrayIconHandle = smallIcons[0];
                    return Icon.FromHandle(_customTrayIconHandle);
                }

                Icon assoc = Icon.ExtractAssociatedIcon(exePath);
                if (assoc != null)
                {
                    return assoc;
                }
            }
            catch
            {
            }
            return SystemIcons.Application;
        }

        private static void UpdateTrayMenu()
        {
            if (_menuTopLeftEnabled != null) _menuTopLeftEnabled.Checked = _enableTopLeft;
            if (_menuTaskView != null)
            {
                _menuTaskView.Enabled = _enableTopLeft;
                _menuTaskView.Checked = _enableTopLeft && (_topLeftAction == CornerAction.TaskView);
            }
            if (_menuAltTabPopup != null)
            {
                _menuAltTabPopup.Enabled = _enableTopLeft;
                _menuAltTabPopup.Checked = _enableTopLeft && (_topLeftAction == CornerAction.WindowSwitcher);
            }
            if (_menuScrollDesktops != null) _menuScrollDesktops.Checked = _enableTaskViewScroll;
            if (_menuF1AltTab != null) _menuF1AltTab.Checked = _enableF1AltTab;
            if (_menuF9F10Desktop != null) _menuF9F10Desktop.Checked = _enableF9F10Desktop;
            if (_menuVolArrows != null) _menuVolArrows.Checked = _enableVolArrows;

            if (_TrayIcon != null)
            {
                if (!_enableTopLeft)
                {
                    _TrayIcon.Text = "Hot Corners (Corner Off | Shortcuts Active)";
                }
                else
                {
                    _TrayIcon.Text = _topLeftAction == CornerAction.TaskView
                        ? "Hot Corners (Top-Left: Task View + Scroll)"
                        : "Hot Corners (Top-Left: Alt+Tab Switcher)";
                }
            }
        }

        private sealed class SystemEventListenerWindow : Form
        {
            public SystemEventListenerWindow()
            {
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Minimized;
                Opacity = 0;
            }

            protected override void SetVisibleCore(bool value)
            {
                base.SetVisibleCore(false);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
                {
                    UpdateScreenMetrics();
                    RepairTaskSwitchingIfMinimized(false);
                }
                base.WndProc(ref m);
            }
        }

        private static void OnApplicationExit(object sender, EventArgs e)
        {
            ReleaseStuckModifiers();

            if (_minimizeHook != IntPtr.Zero)
            {
                UnhookWinEvent(_minimizeHook);
                _minimizeHook = IntPtr.Zero;
            }
            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }
            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            if (_TrayIcon != null)
            {
                _TrayIcon.Visible = false;
                _TrayIcon.Dispose();
            }

            if (_customTrayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_customTrayIconHandle);
                _customTrayIconHandle = IntPtr.Zero;
            }
        }

        // =====================================================================
        // 10. BUILT-IN DIAGNOSTICS (HotCorners.exe --verify)
        // =====================================================================
        private static void RunVerificationDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Windows Hot Corners Verification Report ===");
            sb.AppendFormat("Upper-Left Hot Corner   : {0} ({1})\r\n",
                _enableTopLeft ? "ON" : "OFF",
                _topLeftAction == CornerAction.TaskView ? "Task View / Win+Tab" : "Window Switcher / Ctrl+Alt+Tab");
            sb.AppendFormat("Task View Scroll Switch : {0}\r\n", _enableTaskViewScroll ? "ON" : "OFF");
            sb.AppendFormat("Shortcut F1 -> Alt+Tab  : {0}\r\n", _enableF1AltTab ? "ON" : "OFF");
            sb.AppendFormat("Shortcut F9/F10 -> Desk : {0}\r\n", _enableF9F10Desktop ? "ON" : "OFF");
            sb.AppendFormat("Shortcut Vol -> Arrows  : {0}\r\n", _enableVolArrows ? "ON" : "OFF");
            sb.AppendFormat("Physical Screen Metrics : {0} x {1}\r\n", _screenW, _screenH);
            sb.AppendFormat("Top-Left Hitbox         : {0} x {1} px (Hysteresis: {2}px, Cooldown: {3}ms)\r\n",
                CORNER_W, CORNER_H, RESET_HYSTERESIS_PX, TRIGGER_COOLDOWN_MS);
            sb.AppendFormat("Bottom Corners          : DISABLED (100% Stock Windows Native)\r\n");

            IntPtr hTS = FindWindow("XamlExplorerHostIslandWindow", "Task Switching");
            if (hTS != IntPtr.Zero)
            {
                RECT r; GetWindowRect(hTS, out r);
                WINDOWPLACEMENT wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                GetWindowPlacement(hTS, ref wp);
                sb.AppendFormat("Task Switching HWND     : 0x{0:X} | Rect=({1},{2},{3},{4}) | showCmd={5} | IsIconic={6}\r\n",
                    hTS.ToInt64(), r.Left, r.Top, r.Right, r.Bottom, wp.showCmd, IsIconic(hTS));
            }

            AttachConsole(-1);
            Console.WriteLine("\r\n" + sb.ToString());
            if (Environment.UserInteractive)
            {
                MessageBox.Show(sb.ToString(), "Windows Hot Corners Verification", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
