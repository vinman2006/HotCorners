/*
 * HotCorners.cs — Production-Grade Native C# Hot Corners Daemon for Windows 11 (23H2 / 24H2)
 *
 * COMPILATION COMMAND (.NET Framework 4.8 Built-in Compiler):
 *   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /out:HotCorners.exe HotCorners.cs
 *
 * DIAGNOSTIC / VERIFICATION MODE:
 *   HotCorners.exe --verify
 *
 * ARCHITECTURE & ROOT-CAUSE SUMMARY:
 *   1. Physical Coordinate Space: Calls SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
 *      with fallback to SetProcessDPIAware() so GetSystemMetrics, ClipCursor, SetWindowPos, and MSLLHOOKSTRUCT
 *      all operate in identical 1:1 physical pixels (1920x1200).
 *   2. Compact Corner Hitboxes (Prevents Accidental Switching):
 *      - Tight 45x45px physical corner trigger boxes so virtual desktops and Task View only trigger
 *        when the user DELIBERATELY flicks into the very corner of the screen.
 *      - Cooldown increased to 500ms and Hysteresis set to 75px to eliminate accidental repeat switches.
 *   3. Multi-Layer Corner Immunity:
 *      - Layer A (Pre-Entry Kernel ClipCursor Fence): When cursor enters the corner approach band (X <= 100 or X >= W - 100),
 *        ClipCursor is engaged before the cursor reaches Y = H - 2 (clamping bottom to H - 4, e.g. Y = 1196).
 *      - Layer B (Pure Hook Swallow on First Overshoot Packet): If a high-speed touchpad/mouse packet jumps straight to
 *        Y >= 1200 in a single frame, LowLevelMouseProc returns (IntPtr)1 without calling SetCursorPos.
 *      - Layer C (Topmost Corner Shield Windows): Two 100x6px WS_POPUP windows with WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED
 *        and alpha=1 sit at (0, H - 6) and (W - 100, H - 6) as secondary hit-test barriers.
 *      - Layer D (Post-Desktop-Switch Taskbar Suppression): 350ms post-switch suppression guard keeps Shell_TrayWnd parked at Y = H - 2.
 *   4. 100% Natural Center Bottom Edge (100 < X < W - 100):
 *      - As soon as the cursor is in the center X range, ClipCursor is released and all mouse packets pass through untouched.
 */

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HotCornersDaemon
{
    internal static class Program
    {
        // =====================================================================
        // 1. CONFIGURATION CONSTANTS (Physical Pixels on 1920x1200 @ 125% DPI)
        // =====================================================================
        // Tight, precise corner trigger hitboxes (45x45 px) - prevents accidental triggering!
        private const int TOP_LEFT_WIDTH = 45;
        private const int TOP_LEFT_HEIGHT = 45;

        private const int BOTTOM_CORNER_WIDTH = 45;
        private const int BOTTOM_CORNER_HEIGHT = 45;

        // Protective corner shield zone for taskbar suppression (100px wide)
        private const int CORNER_SHIELD_WIDTH = 100;
        private const int CORNER_FENCE_ARM_MARGIN = 80;

        // Safe Y ceiling (H - 4 = 1196 on 1920x1200). Shell_TrayWnd wakes at Y >= H - 2 (1198).
        private const int SAFE_BOTTOM_OFFSET = 4;

        // Reset hysteresis: user must move cursor 75px out of the corner before it re-arms.
        private const int RESET_HYSTERESIS_PX = 75;

        // Cooldown between consecutive triggers (ms) to eliminate accidental rapid switching.
        private const long TRIGGER_COOLDOWN_MS = 500;

        // Diagonal trajectory lookahead window
        private const int DIAGONAL_X_BUFFER = 60;
        private const int DIAGONAL_MIN_DX = 8;

        // =====================================================================
        // 2. WIN32 API DECLARATIONS
        // =====================================================================
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_ACTIVATEAPP = 0x001C;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;
        private const int HTCLIENT = 1;

        private const uint LLMHF_INJECTED = 0x00000001;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;

        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint WS_EX_LAYERED = 0x00080000;

        private const uint LWA_ALPHA = 0x00000002;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSENDCHANGING = 0x0400;

        private const byte VK_LWIN = 0x5B;
        private const byte VK_TAB = 0x09;
        private const byte VK_LCONTROL = 0xA2;
        private const byte VK_LEFT = 0x25;
        private const byte VK_RIGHT = 0x27;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const uint ABM_GETSTATE = 0x00000004;
        private const uint ABM_GETTASKBARPOS = 0x00000005;
        private const int ABS_AUTOHIDE = 0x0000001;

        private const int QUNS_BUSY = 2;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        private const int QUNS_PRESENTATION_MODE = 4;

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

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
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClipCursor(out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        // =====================================================================
        // 3. DAEMON STATE (Zero allocations inside LowLevelMouseProc)
        // =====================================================================
        private static IntPtr _mouseHookHandle = IntPtr.Zero;
        private static LowLevelMouseProc _mouseHookDelegate;

        private static int _screenW;
        private static int _screenH;
        private static int _safeBottomY; // _screenH - SAFE_BOTTOM_OFFSET (1196 on 1920x1200)
        private static RECT _cornerClipRect;

        private static volatile bool _isClipFenceActive;
        private static int _prevX = -1;
        private static int _prevY = -1;

        private static bool _topLeftArmed = true;
        private static bool _bottomLeftArmed = true;
        private static bool _bottomRightArmed = true;
        private static long _lastTriggerTick;

        private static readonly Stopwatch _Clock = Stopwatch.StartNew();

        private static readonly AutoResetEvent _ActionEvent = new AutoResetEvent(false);
        private static int _PendingAction; // 1 = TaskView, 2 = DesktopLeft, 3 = DesktopRight
        private static volatile bool _Running = true;

        private static string _VirtualDesktopExePath;
        private static CornerShieldWindow _LeftShield;
        private static CornerShieldWindow _RightShield;
        private static NotifyIcon _TrayIcon;

        // =====================================================================
        // 4. ENTRY POINT
        // =====================================================================
        private static string _logPath;
        internal static void Log(string message)
        {
            ThreadPool.QueueUserWorkItem((state) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(_logPath))
                    {
                        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HotCorners.log");
                    }
                    string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, message);
                    File.AppendAllText(_logPath, line);
                }
                catch { }
            });
        }

        [STAThread]
        private static void Main(string[] args)
        {
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HotCorners.log");
            Log("=================================================");
            Log("HotCorners Daemon starting up (Taskbar-Immune v3.1)");

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log("FATAL UNHANDLED EXCEPTION: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null"));
            };

            Application.ThreadException += (s, e) =>
            {
                Log("THREAD EXCEPTION: " + (e.Exception != null ? e.Exception.ToString() : "null"));
            };

            EnablePhysicalDpiAwareness();
            UpdateScreenMetrics();
            Log(string.Format("Physical Screen: {0}x{1}. Safe Bottom Y: {2}. Triggers: {3}x{4}px.",
                _screenW, _screenH, _safeBottomY, BOTTOM_CORNER_WIDTH, BOTTOM_CORNER_HEIGHT));

            if (args != null && args.Length > 0 &&
                (string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "/verify", StringComparison.OrdinalIgnoreCase)))
            {
                RunVerificationDiagnostics();
                return;
            }

            bool createdNew;
            using (Mutex singleInstanceMutex = new Mutex(true, @"Local\Win11HotCornersDaemon_ImmutableCorners", out createdNew))
            {
                if (!createdNew)
                {
                    Log("Another instance is already running (Mutex locked). Exiting.");
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                ResolveVirtualDesktopExePath();
                Log("Resolved VirtualDesktop path: " + _VirtualDesktopExePath);

                // Create Topmost Corner Shields
                try
                {
                    _LeftShield = new CornerShieldWindow(0, _screenH - 6, CORNER_SHIELD_WIDTH, 6);
                    _RightShield = new CornerShieldWindow(_screenW - CORNER_SHIELD_WIDTH, _screenH - 6, CORNER_SHIELD_WIDTH, 6);
                    Log("Corner shield windows successfully created.");
                }
                catch (Exception ex)
                {
                    Log("Warning: Could not create shield windows: " + ex.Message);
                }

                // Background action dispatcher thread
                Thread workerThread = new Thread(ActionWorkerLoop)
                {
                    IsBackground = true,
                    Name = "HotCornersActionWorker",
                    Priority = ThreadPriority.AboveNormal
                };
                workerThread.Start();
                Log("Background action worker thread started.");

                // Install Low-Level Mouse Hook
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                _mouseHookDelegate = HookCallback;
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, GetModuleHandle(curModule.ModuleName), 0);
                }

                Log(string.Format("Low-level mouse hook installed: {0} (Handle: 0x{1:X})",
                    _mouseHookHandle != IntPtr.Zero, _mouseHookHandle.ToInt64()));

                InitializeTrayIcon();

                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) =>
                {
                    UpdateScreenMetrics();
                    Log(string.Format("Display resolution changed -> Updated to {0}x{1}", _screenW, _screenH));
                };

                Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    UpdateScreenMetrics();
                };

                Application.ApplicationExit += OnApplicationExit;
                Log("Entering WinForms message loop (Application.Run)...");
                Application.Run();
                Log("Exited WinForms message loop.");
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
            _safeBottomY = _screenH - SAFE_BOTTOM_OFFSET; // 1196 on 1920x1200

            _cornerClipRect = new RECT
            {
                Left = 0,
                Top = 0,
                Right = _screenW,
                Bottom = _safeBottomY + 1
            };

            if (_LeftShield != null && _RightShield != null)
            {
                _LeftShield.UpdateBounds(0, _screenH - 6, CORNER_SHIELD_WIDTH, 6);
                _RightShield.UpdateBounds(_screenW - CORNER_SHIELD_WIDTH, _screenH - 6, CORNER_SHIELD_WIDTH, 6);
            }
        }

        private static void ResolveVirtualDesktopExePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string candidate1 = Path.Combine(baseDir, "VirtualDesktop.exe");
            if (File.Exists(candidate1))
            {
                _VirtualDesktopExePath = candidate1;
                return;
            }

            string candidate2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsHotCorners", "VirtualDesktop.exe");
            if (File.Exists(candidate2))
            {
                _VirtualDesktopExePath = candidate2;
                return;
            }

            string candidate3 = @"E:\Windows Configurations\VirtualDesktop.exe";
            if (File.Exists(candidate3))
            {
                _VirtualDesktopExePath = candidate3;
                return;
            }

            _VirtualDesktopExePath = "VirtualDesktop.exe";
        }

        // =====================================================================
        // 5. LOW-LEVEL MOUSE HOOK (Zero SetCursorPos, Zero Allocations)
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

                int dx = (_prevX >= 0) ? (x - _prevX) : 0;
                int dy = (_prevY >= 0) ? (y - _prevY) : 0;
                _prevX = x;
                _prevY = y;

                // -----------------------------------------------------------------
                // A. Hysteresis Re-Arming Checks (must move 75px out of corner)
                // -----------------------------------------------------------------
                if (!_topLeftArmed && (x > TOP_LEFT_WIDTH + RESET_HYSTERESIS_PX || y > TOP_LEFT_HEIGHT + RESET_HYSTERESIS_PX))
                {
                    _topLeftArmed = true;
                }
                if (!_bottomLeftArmed && (x > BOTTOM_CORNER_WIDTH + RESET_HYSTERESIS_PX || y < _screenH - BOTTOM_CORNER_HEIGHT - RESET_HYSTERESIS_PX))
                {
                    _bottomLeftArmed = true;
                }
                if (!_bottomRightArmed && (x < _screenW - BOTTOM_CORNER_WIDTH - RESET_HYSTERESIS_PX || y < _screenH - BOTTOM_CORNER_HEIGHT - RESET_HYSTERESIS_PX))
                {
                    _bottomRightArmed = true;
                }

                // -----------------------------------------------------------------
                // B. Corner Taskbar Shielding Management (ClipCursor Fence)
                // -----------------------------------------------------------------
                bool inLeftShieldColumn = (x <= CORNER_SHIELD_WIDTH);
                bool inRightShieldColumn = (x >= _screenW - CORNER_SHIELD_WIDTH);

                bool streakingToLeftCorner = !inLeftShieldColumn &&
                                             (x <= CORNER_SHIELD_WIDTH + DIAGONAL_X_BUFFER) &&
                                             (dx <= -DIAGONAL_MIN_DX) &&
                                             (dy >= 0);

                bool streakingToRightCorner = !inRightShieldColumn &&
                                              (x >= _screenW - CORNER_SHIELD_WIDTH - DIAGONAL_X_BUFFER) &&
                                              (dx >= DIAGONAL_MIN_DX) &&
                                              (dy >= 0);

                bool inBottomApproachBand = (rawY >= _screenH - CORNER_FENCE_ARM_MARGIN);
                bool shouldGuardBottomEdge = inBottomApproachBand &&
                                             (inLeftShieldColumn || inRightShieldColumn || streakingToLeftCorner || streakingToRightCorner);

                if (shouldGuardBottomEdge)
                {
                    if (!_isClipFenceActive)
                    {
                        ClipCursor(ref _cornerClipRect);
                        _isClipFenceActive = true;
                    }
                }
                else
                {
                    // Center Bottom Edge (100 < X < W - 100):
                    // Immediately release ClipCursor so center taskbar unhides smoothly!
                    if (_isClipFenceActive)
                    {
                        ClipCursor(IntPtr.Zero);
                        _isClipFenceActive = false;
                    }
                }

                // -----------------------------------------------------------------
                // C. TIGHT, INTENTIONAL HOT CORNER TRIGGERS (45x45 px)
                // -----------------------------------------------------------------
                long nowMs = (_Clock.ElapsedTicks * 1000L) / Stopwatch.Frequency;
                bool cooldownElapsed = (nowMs - _lastTriggerTick) >= TRIGGER_COOLDOWN_MS;

                // 1. Upper-Left Corner (x <= 45 && y <= 45) -> Task View (Win + Tab)
                if (x <= TOP_LEFT_WIDTH && y <= TOP_LEFT_HEIGHT)
                {
                    if (_topLeftArmed && cooldownElapsed)
                    {
                        _topLeftArmed = false;
                        _lastTriggerTick = nowMs;
                        Interlocked.Exchange(ref _PendingAction, 1);
                        _ActionEvent.Set();
                    }
                }
                // 2. Lower-Left Corner (x <= 45 && y >= H - 45) -> Previous Virtual Desktop
                else if (x <= BOTTOM_CORNER_WIDTH && y >= _screenH - BOTTOM_CORNER_HEIGHT)
                {
                    if (_bottomLeftArmed && cooldownElapsed)
                    {
                        _bottomLeftArmed = false;
                        _lastTriggerTick = nowMs;
                        Interlocked.Exchange(ref _PendingAction, 2);
                        _ActionEvent.Set();
                    }
                }
                // 3. Lower-Right Corner (x >= W - 45 && y >= H - 45) -> Next Virtual Desktop
                else if (x >= _screenW - BOTTOM_CORNER_WIDTH && y >= _screenH - BOTTOM_CORNER_HEIGHT)
                {
                    if (_bottomRightArmed && cooldownElapsed)
                    {
                        _bottomRightArmed = false;
                        _lastTriggerTick = nowMs;
                        Interlocked.Exchange(ref _PendingAction, 3);
                        _ActionEvent.Set();
                    }
                }

                // -----------------------------------------------------------------
                // D. First-Packet Hardware Overshoot Swallow (Layer B)
                // -----------------------------------------------------------------
                if (shouldGuardBottomEdge && rawY > _safeBottomY)
                {
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        // =====================================================================
        // 6. BACKGROUND WORKER & POST-SWITCH TASKBAR SUPPRESSION (Layer D)
        // =====================================================================
        private static void ActionWorkerLoop()
        {
            while (_Running)
            {
                _ActionEvent.WaitOne();
                if (!_Running) break;

                int action = Interlocked.Exchange(ref _PendingAction, 0);
                if (action == 0) continue;

                if (IsMouseButtonPressed() || IsExclusiveFullscreenActive())
                {
                    continue;
                }

                try
                {
                    Log(string.Format("Executing Corner Action {0} ({1})", action, action == 1 ? "Top-Left Task View" : (action == 2 ? "Bottom-Left Slide Left" : "Bottom-Right Slide Right")));
                    if (action == 1)
                    {
                        SendWinTab();
                    }
                    else if (action == 2)
                    {
                        SwitchVirtualDesktop(left: true);
                        SuppressTaskbarPopupDuringDesktopSwitch();
                    }
                    else if (action == 3)
                    {
                        SwitchVirtualDesktop(left: false);
                        SuppressTaskbarPopupDuringDesktopSwitch();
                    }
                }
                catch
                {
                }
            }
        }

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

        private static void SendWinTab()
        {
            keybd_event(VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);
            Thread.Sleep(15);
            keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SwitchVirtualDesktop(bool left)
        {
            string arg = left ? "/Left /Wrap" : "/Right /Wrap";

            if (!string.IsNullOrEmpty(_VirtualDesktopExePath) && File.Exists(_VirtualDesktopExePath))
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = _VirtualDesktopExePath,
                    Arguments = arg,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(250);
                    }
                }
            }
            else
            {
                byte arrowVk = left ? VK_LEFT : VK_RIGHT;
                keybd_event(VK_LCONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                keybd_event(arrowVk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                Thread.Sleep(15);
                keybd_event(arrowVk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        private static void SuppressTaskbarPopupDuringDesktopSwitch()
        {
            IntPtr hTray = FindWindow("Shell_TrayWnd", null);
            if (hTray == IntPtr.Zero) return;

            int parkedY = _screenH - 2; // 1198 on 1920x1200

            for (int i = 0; i < 35; i++)
            {
                POINT pt;
                if (GetCursorPos(out pt))
                {
                    bool stillInCorner = (pt.X <= CORNER_SHIELD_WIDTH + 30) ||
                                         (pt.X >= _screenW - CORNER_SHIELD_WIDTH - 30);
                    if (!stillInCorner && pt.Y >= _screenH - 40)
                    {
                        break;
                    }
                }

                RECT trayRect;
                if (GetWindowRect(hTray, out trayRect))
                {
                    int trayHeight = trayRect.Bottom - trayRect.Top;
                    if (trayRect.Top < parkedY && trayHeight > 0 && trayHeight <= 120)
                    {
                        SetWindowPos(
                            hTray,
                            IntPtr.Zero,
                            trayRect.Left,
                            parkedY,
                            trayRect.Right - trayRect.Left,
                            trayHeight,
                            SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
                    }
                }

                if (_LeftShield != null && _RightShield != null)
                {
                    _LeftShield.RaiseToTopmost();
                    _RightShield.RaiseToTopmost();
                }

                Thread.Sleep(10);
            }
        }

        // =====================================================================
        // 7. TOPMOST NON-ACTIVATING CORNER SHIELD WINDOW (Layer C)
        // =====================================================================
        private sealed class CornerShieldWindow : NativeWindow, IDisposable
        {
            private int _x;
            private int _y;
            private int _w;
            private int _h;

            public CornerShieldWindow(int x, int y, int w, int h)
            {
                _x = x;
                _y = y;
                _w = w;
                _h = h;

                CreateParams cp = new CreateParams
                {
                    Caption = "HotCornersShield",
                    X = _x,
                    Y = _y,
                    Width = _w,
                    Height = _h,
                    Style = unchecked((int)(WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS)),
                    ExStyle = unchecked((int)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED))
                };

                CreateHandle(cp);
                SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
                RaiseToTopmost();
            }

            public void UpdateBounds(int x, int y, int w, int h)
            {
                _x = x;
                _y = y;
                _w = w;
                _h = h;
                if (Handle != IntPtr.Zero)
                {
                    SetWindowPos(Handle, HWND_TOPMOST, _x, _y, _w, _h, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
                }
            }

            public void RaiseToTopmost()
            {
                if (Handle != IntPtr.Zero)
                {
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEACTIVATE)
                {
                    m.Result = (IntPtr)MA_NOACTIVATE;
                    return;
                }
                if (m.Msg == WM_NCHITTEST)
                {
                    m.Result = (IntPtr)HTCLIENT;
                    return;
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    DestroyHandle();
                }
            }
        }

        // =====================================================================
        // 8. SYSTEM TRAY ICON & CLEANUP
        // =====================================================================

        private static void InitializeTrayIcon()
        {
            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("HotCorners Daemon (Active - 1920x1200)").Enabled = false;
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Exit HotCorners", (s, e) => Application.Exit());

            _TrayIcon = new NotifyIcon
            {
                Text = "HotCorners Win11 Daemon (Taskbar Immune)",
                Icon = SystemIcons.Shield,
                ContextMenu = trayMenu,
                Visible = true
            };
        }

        private static void OnApplicationExit(object sender, EventArgs e)
        {
            _Running = false;
            _ActionEvent.Set();

            ClipCursor(IntPtr.Zero);

            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            if (_LeftShield != null) _LeftShield.Dispose();
            if (_RightShield != null) _RightShield.Dispose();

            if (_TrayIcon != null)
            {
                _TrayIcon.Visible = false;
                _TrayIcon.Dispose();
            }
        }

        // =====================================================================
        // 9. BUILT-IN DIAGNOSTIC VERIFICATION HARNESS (HotCorners.exe --verify)
        // =====================================================================
        private static void RunVerificationDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== HotCorners Windows 11 Taskbar Immunity Verification ===");
            sb.AppendFormat("Physical Screen Metrics : {0} x {1}\r\n", _screenW, _screenH);
            sb.AppendFormat("Corner Trigger Box      : {0} x {1} px (Tight Corners)\r\n", BOTTOM_CORNER_WIDTH, BOTTOM_CORNER_HEIGHT);
            sb.AppendFormat("Safe Corner Y Ceiling   : Y <= {0} (Taskbar sensor is at Y >= {1})\r\n", _safeBottomY, _screenH - 2);

            IntPtr hTray = FindWindow("Shell_TrayWnd", null);
            RECT trayRect;
            if (hTray != IntPtr.Zero && GetWindowRect(hTray, out trayRect))
            {
                sb.AppendFormat("Shell_TrayWnd Handle    : 0x{0:X} | Rect: ({1}, {2}) - ({3}, {4})\r\n",
                    hTray.ToInt64(), trayRect.Left, trayRect.Top, trayRect.Right, trayRect.Bottom);
            }

            APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
            uint state = (uint)SHAppBarMessage(ABM_GETSTATE, ref abd).ToInt64();
            sb.AppendFormat("Taskbar Auto-Hide State : {0}\r\n\r\n", (state & ABS_AUTOHIDE) != 0 ? "ENABLED (ABS_AUTOHIDE)" : "DISABLED");

            ClipCursor(ref _cornerClipRect);
            RECT activeClip;
            GetClipCursor(out activeClip);
            sb.AppendFormat("[TEST 1] Corner ClipCursor Fence Active -> Bottom Bound = {0} (Max reachable Y = {1})\r\n",
                activeClip.Bottom, activeClip.Bottom - 1);

            ClipCursor(IntPtr.Zero);
            GetClipCursor(out activeClip);
            sb.AppendFormat("[TEST 2] Center Bottom Edge Released    -> Bottom Bound = {0} (Max reachable Y = {1})\r\n",
                activeClip.Bottom, activeClip.Bottom - 1);

            MessageBox.Show(sb.ToString(), "HotCorners Verification Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
