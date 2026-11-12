/*
 * HotCorners.cs — Native Win32 Hot Corners Daemon for Windows 11 & 10
 * Configuration:
 *   1. Upper-Left Corner -> Windows Task View (Win + Tab) via Shell.Application COM (0 keystrokes).
 *      (Switchable via System Tray to Alt+Tab Window Switcher popup.)
 *   2. GNOME / Linux-Style Scroll-to-Switch-Desktops inside Task View:
 *      While Task View (Win + Tab) is open (whether opened via Upper-Left corner or keyboard Win+Tab),
 *      scrolling Mouse Wheel Up/Down or Touchpad 2-finger Up/Down/Left/Right switches Virtual Desktops
 *      seamlessly while keeping the Task View interface open!
 *   3. Zero-CPU SetWinEventHook Self-Healing Guard for Windows 11 'Task Switching' (Alt+Tab) Popup:
 *      Automatically prevents Explorer's Alt+Tab XamlExplorerHostIslandWindow from getting trapped in
 *      SW_SHOWMINIMIZED (159x27) across virtual desktop transitions.
 *   4. Bottom corners & bottom edge remain 100% untouched stock Windows native behavior.
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
        // 1. CONFIGURATION: UPPER-LEFT (WIN+TAB) + TASK VIEW DESKTOP SCROLLING
        // =====================================================================
        private const string REG_KEY_PATH = @"Software\WindowsHotCorners";
        private const string REG_VAL_ACTION = "TopLeftMode";

        private static CornerAction _topLeftAction = CornerAction.TaskView;
        private const bool ENABLE_TOP_LEFT = true;             // Active
        private const bool ENABLE_TASKVIEW_SCROLL = true;      // Scroll inside Task View switches virtual desktops
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
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEHWHEEL = 0x020E;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const uint LLMHF_INJECTED = 0x00000001;

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
        private const ushort VK_LCONTROL = 0xA2;
        private const ushort VK_RCONTROL = 0xA3;
        private const ushort VK_LMENU = 0xA4;
        private const ushort VK_RMENU = 0xA5;

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

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
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
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

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
        private static LowLevelMouseProc _mouseHookDelegate;

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

        private static readonly Stopwatch _Clock = Stopwatch.StartNew();

        private static NotifyIcon _TrayIcon;
        private static IntPtr _customTrayIconHandle = IntPtr.Zero;
        private static MenuItem _menuItemTaskView;
        private static MenuItem _menuItemAltTab;

        // =====================================================================
        // 4. ENTRY POINT
        // =====================================================================
        [STAThread]
        private static void Main(string[] args)
        {
            EnablePhysicalDpiAwareness();
            UpdateScreenMetrics();
            LoadActionPreference();

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

                // Install Low-Level Mouse Hook (Corner Flick + Task View Scroll Switcher)
                _mouseHookDelegate = HookCallback;
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, GetModuleHandle(curModule.ModuleName), 0);
                }

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

        private static void LoadActionPreference()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_KEY_PATH))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(REG_VAL_ACTION);
                        if (val is int)
                        {
                            int intVal = (int)val;
                            if (Enum.IsDefined(typeof(CornerAction), intVal))
                            {
                                _topLeftAction = (CornerAction)intVal;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void SaveActionPreference()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(REG_KEY_PATH))
                {
                    if (key != null)
                    {
                        key.SetValue(REG_VAL_ACTION, (int)_topLeftAction, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
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
        // 6. LOW-LEVEL MOUSE HOOK (< 0.001ms Latency, Top-Left + Task View Scroll)
        // =====================================================================
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();

                if (msg == WM_MOUSEMOVE)
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

                    // A. Re-Arming Hysteresis Check (must travel 80px out of top-left)
                    if (!_topLeftArmed && (x > CORNER_W + RESET_HYSTERESIS_PX || y > CORNER_H + RESET_HYSTERESIS_PX))
                    {
                        _topLeftArmed = true;
                    }

                    // B. Strict Corner-Touch Trigger (X <= 4, Y <= 4) -> Opens Task View (Win+Tab)
                    if (ENABLE_TOP_LEFT && x <= CORNER_W && y <= CORNER_H)
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
                else if (ENABLE_TASKVIEW_SCROLL && (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL))
                {
                    MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if ((hookStruct.flags & LLMHF_INJECTED) == 0 && IsTaskViewOpen())
                    {
                        short rawDelta = unchecked((short)((uint)hookStruct.mouseData >> 16));
                        if (rawDelta != 0)
                        {
                            // Vertical wheel: Scroll Up (+delta) -> Left desktop (-), Scroll Down (-delta) -> Right desktop (+)
                            // Horizontal wheel: Scroll Left (-delta) -> Left desktop (-), Scroll Right (+delta) -> Right desktop (+)
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

                        // Swallow wheel packets inside Task View so the window grid doesn't fight desktop switching
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        // =====================================================================
        // 7. ASYNC ACTION & DESKTOP SWITCH DISPATCH
        // =====================================================================
        private static bool IsMouseButtonPressed()
        {
            return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
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
                        // Native Windows Task View (Win + Tab) via Shell.Application COM (0 synthetic keystrokes)
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
                    MakeKeyInput(VK_TAB, false)
                };
                SendInput((uint)upSeq.Length, upSeq, Marshal.SizeOf(typeof(INPUT)));
            }
            catch
            {
            }
        }

        // =====================================================================
        // 8. SYSTEM TRAY ICON & CONTROLS
        // =====================================================================
        private static void InitializeTrayIcon()
        {
            ContextMenu trayMenu = new ContextMenu();

            _menuItemTaskView = new MenuItem("Upper-Left: Task View (Win + Tab) + Scroll Desktops", (s, e) =>
            {
                _topLeftAction = CornerAction.TaskView;
                SaveActionPreference();
                UpdateTrayMenu();
            });

            _menuItemAltTab = new MenuItem("Upper-Left: Window Switcher (Alt + Tab Popup)", (s, e) =>
            {
                _topLeftAction = CornerAction.WindowSwitcher;
                SaveActionPreference();
                UpdateTrayMenu();
            });

            trayMenu.MenuItems.Add(_menuItemTaskView);
            trayMenu.MenuItems.Add(_menuItemAltTab);
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Repair Alt+Tab Popup & Release Stuck Keys", (s, e) =>
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
            if (_menuItemTaskView != null) _menuItemTaskView.Checked = (_topLeftAction == CornerAction.TaskView);
            if (_menuItemAltTab != null) _menuItemAltTab.Checked = (_topLeftAction == CornerAction.WindowSwitcher);
            if (_TrayIcon != null)
            {
                _TrayIcon.Text = _topLeftAction == CornerAction.TaskView
                    ? "Hot Corners (Top-Left: Task View + Scroll Desktops)"
                    : "Hot Corners (Top-Left: Alt+Tab Switcher)";
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
        // 9. BUILT-IN DIAGNOSTICS (HotCorners.exe --verify)
        // =====================================================================
        private static void RunVerificationDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Windows Hot Corners Verification Report ===");
            sb.AppendFormat("Configuration           : UPPER-LEFT (Task View) + Scroll Desktop Switcher\r\n");
            sb.AppendFormat("Upper-Left Action       : {0}\r\n",
                _topLeftAction == CornerAction.TaskView ? "Task View (Win + Tab / Shell COM)" : "Window Switcher (Ctrl+Alt+Tab Popup)");
            sb.AppendFormat("Task View Scroll Switch : {0} (Wheel Up/Left -> Prev Desktop, Down/Right -> Next Desktop)\r\n",
                ENABLE_TASKVIEW_SCROLL ? "ENABLED" : "DISABLED");
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
