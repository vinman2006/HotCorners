/*
 * HotCorners.cs — Native Win32 Hot Corners Daemon for Windows 11 & 10
 * Configuration: Single Top-Left Hot Corner + Self-Healing Alt+Tab Task Switching Guard
 *
 * Features:
 *   - Only Upper-Left Corner is active; bottom corners & bottom edge remain 100% stock native Windows.
 *   - Two accurate Upper-Left modes (persisted in HKCU & switchable via System Tray):
 *       1) Window Switcher (Alt + Tab): Pops out the native Windows 'Task Switching' grid via
 *          atomic SendInput (Ctrl + Alt + Tab with hardware scan codes).
 *       2) Task View (Win + Tab): Launches native Task View via Shell.Application COM (0 keystrokes).
 *   - Built-in Zero-CPU SetWinEventHook Guard (EVENT_SYSTEM_MINIMIZESTART + EVENT_SYSTEM_FOREGROUND):
 *     On Windows 11 (24H2/26H2), Explorer's singleton 'Task Switching' XamlExplorerHostIslandWindow
 *     can occasionally get trapped in SW_SHOWMINIMIZED (159x27) after Virtual Desktop transitions,
 *     causing Alt+Tab to switch windows invisibly without popping out the switcher overlay.
 *     HotCorners detects and repairs this in-place (cloaked SW_SHOWNOACTIVATE -> SW_HIDE) with 0 flicker.
 *   - 0.0% CPU, Per-Monitor V2 DPI awareness (1:1 physical pixel precision), D3D fullscreen & drag guards.
 *
 * Compilation (.NET Framework 4.8 Built-in Compiler):
 *   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /out:HotCorners.exe HotCorners.cs
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
        WindowSwitcher = 0, // Native Alt+Tab 'Task Switching' popup (Ctrl + Alt + Tab)
        TaskView = 1        // Native 'Task View' (Shell.Application.WindowSwitcher / Win + Tab)
    }

    internal static class Program
    {
        // =====================================================================
        // 1. CONFIGURATION: UPPER-LEFT CORNER ONLY
        // =====================================================================
        private const string REG_KEY_PATH = @"Software\WindowsHotCorners";
        private const string REG_VAL_ACTION = "TopLeftAction";

        private static CornerAction _topLeftAction = CornerAction.WindowSwitcher;
        private const bool ENABLE_TOP_LEFT = true;       // Active
        private const bool ENABLE_BOTTOM_LEFT = false;   // Disabled (Stock native Windows)
        private const bool ENABLE_BOTTOM_RIGHT = false;  // Disabled (Stock native Windows)

        private const int CORNER_W = 50;                 // Top-left trigger hitbox width (px)
        private const int CORNER_H = 45;                 // Top-left trigger hitbox height (px)
        private const int RESET_HYSTERESIS_PX = 75;      // Distance out of corner to re-arm (px)
        private const long TRIGGER_COOLDOWN_MS = 400;    // Cooldown between consecutive triggers (ms)

        // =====================================================================
        // 2. WIN32 API DECLARATIONS
        // =====================================================================
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const uint LLMHF_INJECTED = 0x00000001;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private const ushort VK_TAB = 0x09;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const ushort VK_LCONTROL = 0xA2;
        private const ushort VK_RCONTROL = 0xA3;
        private const ushort VK_LMENU = 0xA4;
        private const ushort VK_RMENU = 0xA5;

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
        private static readonly Stopwatch _Clock = Stopwatch.StartNew();

        private static NotifyIcon _TrayIcon;
        private static MenuItem _menuItemAltTab;
        private static MenuItem _menuItemTaskView;

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

                // Install Low-Level Mouse Hook
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
        // 5. SELF-HEALING 'TASK SWITCHING' ALT+TAB OVERLAY GUARD
        // =====================================================================
        private static void OnWinEvent(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd == IntPtr.Zero || idObject != 0)
            {
                return;
            }

            if (IsTaskSwitchingWindow(hwnd) && IsIconic(hwnd))
            {
                bool keepVisibleIfActivating = (eventType == EVENT_SYSTEM_FOREGROUND);
                RepairTaskSwitchingHwnd(hwnd, keepVisibleIfActivating);
            }
        }

        private static bool IsTaskSwitchingWindow(IntPtr hwnd)
        {
            StringBuilder cls = new StringBuilder(64);
            if (GetClassName(hwnd, cls, cls.Capacity) == 0 ||
                !string.Equals(cls.ToString(), "XamlExplorerHostIslandWindow", StringComparison.Ordinal))
            {
                return false;
            }

            StringBuilder title = new StringBuilder(64);
            GetWindowText(hwnd, title, title.Capacity);
            return string.Equals(title.ToString(), "Task Switching", StringComparison.Ordinal);
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
                    // Cloak before restoring so DWM shows 0 frames on screen
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

        // =====================================================================
        // 6. LOW-LEVEL MOUSE HOOK (< 0.001ms Latency, Top-Left Only)
        // =====================================================================
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
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

                // A. Re-Arming Hysteresis Check (must travel 75px out of top-left)
                if (!_topLeftArmed && (x > CORNER_W + RESET_HYSTERESIS_PX || y > CORNER_H + RESET_HYSTERESIS_PX))
                {
                    _topLeftArmed = true;
                }

                // B. Top-Left Corner Trigger (X <= 50, Y <= 45)
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

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        // =====================================================================
        // 7. ASYNC TOP-LEFT ACTION DISPATCH
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

        private static INPUT MakeKeyInput(ushort vk, bool isDown)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = (ushort)MapVirtualKey(vk, 0),
                        dwFlags = isDown ? 0u : KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
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
                    if (action == CornerAction.WindowSwitcher)
                    {
                        // First guarantee Explorer's 'Task Switching' window isn't minimized (showCmd=2)
                        RepairTaskSwitchingIfMinimized(false);

                        // Send atomic Ctrl+Alt+Tab so the Alt+Tab Task Switching overlay pops out and stays open for mouse selection
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
                    else
                    {
                        // Native Task View (Win + Tab) via Shell.Application COM (0 synthetic keystrokes)
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

            _menuItemAltTab = new MenuItem("Upper-Left: Window Switcher (Alt + Tab Popup)", (s, e) =>
            {
                _topLeftAction = CornerAction.WindowSwitcher;
                SaveActionPreference();
                UpdateTrayMenu();
            });

            _menuItemTaskView = new MenuItem("Upper-Left: Task View (Win + Tab)", (s, e) =>
            {
                _topLeftAction = CornerAction.TaskView;
                SaveActionPreference();
                UpdateTrayMenu();
            });

            trayMenu.MenuItems.Add(_menuItemAltTab);
            trayMenu.MenuItems.Add(_menuItemTaskView);
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
                Icon = SystemIcons.Application,
                ContextMenu = trayMenu,
                Visible = true
            };

            UpdateTrayMenu();
        }

        private static void UpdateTrayMenu()
        {
            if (_menuItemAltTab != null) _menuItemAltTab.Checked = (_topLeftAction == CornerAction.WindowSwitcher);
            if (_menuItemTaskView != null) _menuItemTaskView.Checked = (_topLeftAction == CornerAction.TaskView);
            if (_TrayIcon != null)
            {
                _TrayIcon.Text = _topLeftAction == CornerAction.WindowSwitcher
                    ? "Hot Corners (Top-Left: Alt+Tab Switcher)"
                    : "Hot Corners (Top-Left: Task View)";
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
        }

        // =====================================================================
        // 9. BUILT-IN DIAGNOSTICS (HotCorners.exe --verify)
        // =====================================================================
        private static void RunVerificationDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Windows Hot Corners Verification Report ===");
            sb.AppendFormat("Configuration           : UPPER-LEFT CORNER ONLY + Alt+Tab Guard\r\n");
            sb.AppendFormat("Upper-Left Action       : {0}\r\n",
                _topLeftAction == CornerAction.WindowSwitcher ? "Window Switcher (Ctrl+Alt+Tab Popup)" : "Task View (Shell.Application)");
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
            else
            {
                sb.AppendLine("Task Switching HWND     : Not yet spawned (normal until first Alt+Tab)");
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
