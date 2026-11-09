/*
 * HotCorners.cs — Production-Grade Native C# Hot Corners Daemon for Windows 11 (v3.3 Unified Architecture)
 *
 * COMPILATION COMMAND (.NET Framework 4.8 Built-in Compiler):
 *   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /out:HotCorners.exe HotCorners.cs
 *
 * DIAGNOSTIC / VERIFICATION MODE:
 *   HotCorners.exe --verify
 *
 * UNIFIED 4-LAYER ARCHITECTURE (Combining v3.2 Empirical Research + Kernel ClipCursor Safety):
 *   1. Layer 1 — Switch Animation Suppression (/Animation:0 /Quiet):
 *      Calls VirtualDesktop.exe with "/Animation:0 /Quiet /Left /Wrap" (or "/Right /Wrap") and CreateNoWindow = true.
 *      This suppresses the Windows 11 DWM/XAML desktop name banner that otherwise renders over the bottom-center
 *      edge and steals foreground focus for Shell_TrayWnd (https://doxygen.reactos.org/d3/d8f/traywnd_8cpp_source.html).
 *   2. Layer 2 — 260px Wide Spatial Shield Wings & Tight 60x45px Intentional Corner Hitboxes:
 *      - Trigger Hitboxes: 60x45px in Top-Left, Bottom-Left, and Bottom-Right (with 75px hysteresis & 400ms cooldown).
 *      - Immunity Wings: Left wing (0 <= X <= 260) and Right wing (W - 260 <= X <= W) with a 1400px unrestricted
 *        center bottom edge (260 < X < 1660 on 1920x1200).
 *   3. Layer 3 — Kernel ClipCursor Clamping at Y = H - 10 (1190) + First-Packet Pure Swallow (Zero SetCursorPos):
 *      - Instead of calling SetCursorPos(rawX, H - 10) inside LowLevelMouseProc (https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)
 *        (which injects recursive LLMHF_INJECTED packets and risks LowLevelHooksTimeout hook drops), we arm a kernel
 *        ClipCursor (https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clipcursor) fence at Y <= H - 10 (1190,
 *        8px safely above the Y = 1198 sensor strip) whenever the cursor is in a 260px corner wing and Y >= H - 120.
 *      - Only packets that attempt to cross Y > H - 10 (1190) inside a corner wing are swallowed (return 1), so horizontal
 *        and upward cursor movement inside the bottom 30px of the corners never feels frozen or sticky!
 *   4. Layer 4 — Post-Switch Taskbar Auto-Hide Enforcer (SetWindowPos):
 *      - After each desktop transition, clamps Shell_TrayWnd back to Top = H - 2 (1198) using
 *        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING without stealing window focus.
 */

using System;
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
        // 1. CUSTOMIZABLE SPATIAL & TIMING CONSTANTS (Physical Pixels @ 1920x1200)
        // =====================================================================
        private const int CORNER_W = 60;              // Deliberate corner trigger hitbox width (px)
        private const int CORNER_H = 45;              // Deliberate corner trigger hitbox height (px)
        private const int CORNER_SHIELD_W = 260;      // Taskbar immunity shield wing width (px) — leaves 1400px center
        private const int CORNER_SHIELD_H = 30;       // Bottom approach shield zone height (px) (Y >= 1170)
        private const int CORNER_FENCE_PREARM_H = 120;// Pre-arm height for kernel ClipCursor fence (Y >= 1080)
        private const int SAFE_CLAMP_OFFSET = 10;     // Clamps corner Y at H - 10 (1190), 8px above Y = 1198 sensor
        private const int RESET_HYSTERESIS_PX = 75;   // Distance out of corner hitbox required to re-arm (px)
        private const long TRIGGER_COOLDOWN_MS = 400; // Debounce cooldown between triggers (ms)

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
        private const uint SWP_NOZORDER = 0x0004;
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
        private const int ABS_AUTOHIDE = 0x0000001;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

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
        private static int _safeCornerY; // _screenH - SAFE_CLAMP_OFFSET (1190 on 1920x1200)
        private static RECT _cornerClipRect;

        private static volatile bool _isClipFenceActive;

        private static bool _topLeftArmed = true;
        private static bool _bottomLeftArmed = true;
        private static bool _bottomRightArmed = true;
        private static long _lastTriggerTick;

        private static readonly Stopwatch _Clock = Stopwatch.StartNew();

        private static readonly AutoResetEvent _ActionEvent = new AutoResetEvent(false);
        private static int _PendingAction; // 1 = TaskView, 2 = DesktopLeft, 3 = DesktopRight (synchronized via Interlocked)
        private static volatile bool _Running = true;

        private static string _VirtualDesktopExePath;
        private static CornerShieldWindow _LeftShield;
        private static CornerShieldWindow _RightShield;
        private static NotifyIcon _TrayIcon;

        // =====================================================================
        // 4. ENTRY POINT
        // =====================================================================
        [STAThread]
        private static void Main(string[] args)
        {
            EnablePhysicalDpiAwareness();
            UpdateScreenMetrics();

            if (args != null && args.Length > 0 &&
                (string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "/verify", StringComparison.OrdinalIgnoreCase)))
            {
                RunVerificationDiagnostics();
                return;
            }

            bool createdNew;
            using (Mutex singleInstanceMutex = new Mutex(true, @"Local\Win11HotCornersDaemon_v3_3", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                ResolveVirtualDesktopExePath();

                // Topmost Non-Activating 260px Corner Shield Windows covering the bottom 6px strip
                _LeftShield = new CornerShieldWindow(0, _screenH - 6, CORNER_SHIELD_W, 6);
                _RightShield = new CornerShieldWindow(_screenW - CORNER_SHIELD_W, _screenH - 6, CORNER_SHIELD_W, 6);

                Thread workerThread = new Thread(ActionWorkerLoop)
                {
                    IsBackground = true,
                    Name = "HotCornersActionWorker",
                    Priority = ThreadPriority.AboveNormal
                };
                workerThread.Start();

                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                Thread.CurrentThread.Priority = ThreadPriority.Highest;

                _mouseHookDelegate = HookCallback;
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, GetModuleHandle(curModule.ModuleName), 0);
                }

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
            _safeCornerY = _screenH - SAFE_CLAMP_OFFSET; // 1190 on 1920x1200 (8px above Y = 1198 sensor)

            // ClipCursor RECT Bottom is exclusive, so Bottom = _safeCornerY + 1 confines cursor to Y <= 1190
            _cornerClipRect = new RECT
            {
                Left = 0,
                Top = 0,
                Right = _screenW,
                Bottom = _safeCornerY + 1
            };

            if (_LeftShield != null && _RightShield != null)
            {
                _LeftShield.UpdateBounds(0, _screenH - 6, CORNER_SHIELD_W, 6);
                _RightShield.UpdateBounds(_screenW - CORNER_SHIELD_W, _screenH - 6, CORNER_SHIELD_W, 6);
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

            string appDataCandidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowsHotCorners",
                "VirtualDesktop.exe");
            if (File.Exists(appDataCandidate))
            {
                _VirtualDesktopExePath = appDataCandidate;
                return;
            }

            _VirtualDesktopExePath = "VirtualDesktop.exe";
        }

        // =====================================================================
        // 5. LOW-LEVEL MOUSE HOOK (< 1 microsecond latency, Zero SetCursorPos)
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

                // -----------------------------------------------------------------
                // A. Hysteresis Exit Boundary (75px outside 60x45px corner hitboxes)
                // -----------------------------------------------------------------
                if (!_topLeftArmed && (x > CORNER_W + RESET_HYSTERESIS_PX || y > CORNER_H + RESET_HYSTERESIS_PX))
                {
                    _topLeftArmed = true;
                }
                if (!_bottomLeftArmed && (x > CORNER_W + RESET_HYSTERESIS_PX || y < _screenH - CORNER_H - RESET_HYSTERESIS_PX))
                {
                    _bottomLeftArmed = true;
                }
                if (!_bottomRightArmed && (x < _screenW - CORNER_W - RESET_HYSTERESIS_PX || y < _screenH - CORNER_H - RESET_HYSTERESIS_PX))
                {
                    _bottomRightArmed = true;
                }

                // -----------------------------------------------------------------
                // B. 260px Spatial Shield Wings vs. 1400px Unrestricted Center Edge
                // -----------------------------------------------------------------
                bool inCornerShieldWing = (x <= CORNER_SHIELD_W) || (x >= _screenW - CORNER_SHIELD_W);
                bool inPreArmBand = (rawY >= _screenH - CORNER_FENCE_PREARM_H);

                if (inCornerShieldWing && inPreArmBand)
                {
                    if (!_isClipFenceActive)
                    {
                        ClipCursor(ref _cornerClipRect);
                        _isClipFenceActive = true;
                    }
                }
                else
                {
                    // Cursor is in the 1400px Center Bottom Edge (260 < X < 1660) or upper workspace:
                    // Release ClipCursor immediately so center taskbar auto-hide is 100% natural!
                    if (_isClipFenceActive)
                    {
                        ClipCursor(IntPtr.Zero);
                        _isClipFenceActive = false;
                    }
                }

                // -----------------------------------------------------------------
                // C. Deliberate 60x45px Corner Hitbox Trigger Dispatch
                // -----------------------------------------------------------------
                long nowMs = (_Clock.ElapsedTicks * 1000L) / Stopwatch.Frequency;
                bool cooldownElapsed = (nowMs - _lastTriggerTick) >= TRIGGER_COOLDOWN_MS;

                // 1. Top-Left (X <= 60, Y <= 45) -> Task View (Win + Tab)
                if (x <= CORNER_W && y <= CORNER_H)
                {
                    if (_topLeftArmed && cooldownElapsed)
                    {
                        _topLeftArmed = false;
                        _lastTriggerTick = nowMs;
                        _PendingAction = 1;
                        _ActionEvent.Set();
                    }
                }
                // 2. Bottom-Left (X <= 60, Y >= H - 45) -> Previous Desktop
                else if (x <= CORNER_W && rawY >= _screenH - CORNER_H)
                {
                    if (_bottomLeftArmed && cooldownElapsed)
                    {
                        _bottomLeftArmed = false;
                        _lastTriggerTick = nowMs;
                        _PendingAction = 2;
                        _ActionEvent.Set();
                    }
                }
                // 3. Bottom-Right (X >= W - 60, Y >= H - 45) -> Next Desktop
                else if (x >= _screenW - CORNER_W && rawY >= _screenH - CORNER_H)
                {
                    if (_bottomRightArmed && cooldownElapsed)
                    {
                        _bottomRightArmed = false;
                        _lastTriggerTick = nowMs;
                        _PendingAction = 3;
                        _ActionEvent.Set();
                    }
                }

                // -----------------------------------------------------------------
                // D. Swallow ONLY Overshoot Packets Beyond Y = H - 10 (1190) in Corner Wings
                // -----------------------------------------------------------------
                // Unlike swallowing all moves at Y >= 1170 (which freezes X movement inside the bottom 30px),
                // swallowing ONLY when rawY > _safeCornerY (1190) while ClipCursor holds Y <= 1190 keeps
                // horizontal and upward movement completely fluid while blocking Y >= 1198 sensor contact!
                if (inCornerShieldWing && rawY > _safeCornerY)
                {
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        // =====================================================================
        // 6. ASYNC WORKER: /Animation:0 SWITCHING & POST-SWITCH CLAMP
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
                    if (action == 1)
                    {
                        SendWinTab();
                    }
                    else if (action == 2)
                    {
                        SwitchVirtualDesktop(left: true);
                        ClampTaskbarPostSwitch();
                    }
                    else if (action == 3)
                    {
                        SwitchVirtualDesktop(left: false);
                        ClampTaskbarPostSwitch();
                    }
                }
                catch
                {
                    // Ignore transient desktop transition errors
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
            // Layer 1 Breakthrough: /Animation:0 /Quiet suppresses the Windows 11 desktop name overlay banner
            // that otherwise renders over the bottom-center edge and locks Shell_TrayWnd open with foreground focus!
            string arg = left ? "/Animation:0 /Quiet /Left /Wrap" : "/Animation:0 /Quiet /Right /Wrap";

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
                        proc.WaitForExit(200);
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

        /// <summary>
        /// Layer 4 Safety Net: Clamps Shell_TrayWnd back to Y = H - 2 (1198) if Explorer attempts
        /// to unhide it during a virtual desktop switch, using SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING.
        /// </summary>
        private static void ClampTaskbarPostSwitch()
        {
            IntPtr hTray = FindWindow("Shell_TrayWnd", null);
            if (hTray == IntPtr.Zero) return;

            int parkedY = _screenH - 2; // 1198 on 1920x1200

            for (int i = 0; i < 25; i++)
            {
                POINT pt;
                if (GetCursorPos(out pt))
                {
                    bool inCenterEdge = (pt.X > CORNER_SHIELD_W) &&
                                        (pt.X < _screenW - CORNER_SHIELD_W) &&
                                        (pt.Y >= _screenH - CORNER_SHIELD_H);
                    if (inCenterEdge)
                    {
                        break;
                    }
                }

                RECT trayRect;
                if (GetWindowRect(hTray, out trayRect) && trayRect.Top < _screenH - SAFE_CLAMP_OFFSET)
                {
                    SetWindowPos(
                        hTray,
                        IntPtr.Zero,
                        0,
                        parkedY,
                        0,
                        0,
                        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                }

                Thread.Sleep(10);
            }
        }

        // =====================================================================
        // 7. TOPMOST NON-ACTIVATING CORNER SHIELD WINDOW
        // =====================================================================
        private sealed class CornerShieldWindow : NativeWindow, IDisposable
        {
            public CornerShieldWindow(int x, int y, int w, int h)
            {
                CreateParams cp = new CreateParams
                {
                    Caption = "HotCornersShield",
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    Style = unchecked((int)(WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS)),
                    ExStyle = unchecked((int)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED))
                };

                CreateHandle(cp);
                SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
                SetWindowPos(Handle, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
            }

            public void UpdateBounds(int x, int y, int w, int h)
            {
                if (Handle != IntPtr.Zero)
                {
                    SetWindowPos(Handle, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
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
        // 8. SYSTEM EVENT LISTENER & CLEANUP
        // =====================================================================
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
                }
                else if (m.Msg == WM_ACTIVATEAPP && !_isClipFenceActive)
                {
                    ClipCursor(IntPtr.Zero);
                }
                base.WndProc(ref m);
            }
        }

        private static void InitializeTrayIcon()
        {
            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Windows Hot Corners v3.3 (Active)").Enabled = false;
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Exit Hot Corners", (s, e) => Application.Exit());

            _TrayIcon = new NotifyIcon
            {
                Text = "Windows Hot Corners v3.3 (260px Corner Immunity + /Animation:0)",
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
            sb.AppendLine("=== Windows Hot Corners v3.3 Verification Report ===");
            sb.AppendFormat("Physical Screen Metrics : {0} x {1}\r\n", _screenW, _screenH);
            sb.AppendFormat("Corner Trigger Hitboxes : {0} x {1} px (Hysteresis: {2}px, Cooldown: {3}ms)\r\n",
                CORNER_W, CORNER_H, RESET_HYSTERESIS_PX, TRIGGER_COOLDOWN_MS);
            sb.AppendFormat("Corner Immunity Wings   : Left [0..{0}], Right [{1}..{2}] (Center Span: {3}px)\r\n",
                CORNER_SHIELD_W, _screenW - CORNER_SHIELD_W, _screenW, _screenW - (2 * CORNER_SHIELD_W));
            sb.AppendFormat("Safe Corner Y Ceiling   : Y <= {0} (8px above Y = {1} wake sensor)\r\n\r\n",
                _safeCornerY, _screenH - 2);

            IntPtr hTray = FindWindow("Shell_TrayWnd", null);
            RECT trayRect;
            if (hTray != IntPtr.Zero && GetWindowRect(hTray, out trayRect))
            {
                sb.AppendFormat("Shell_TrayWnd Handle    : 0x{0:X} | Rect: ({1}, {2}) - ({3}, {4})\r\n",
                    hTray.ToInt64(), trayRect.Left, trayRect.Top, trayRect.Right, trayRect.Bottom);
            }

            APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
            uint state = (uint)SHAppBarMessage(ABM_GETSTATE, ref abd).ToInt64();
            sb.AppendFormat("Taskbar Auto-Hide State : {0}\r\n", (state & ABS_AUTOHIDE) != 0 ? "ENABLED (ABS_AUTOHIDE)" : "DISABLED");

            AttachConsole(-1);
            Console.WriteLine(sb.ToString());
        }
    }
}