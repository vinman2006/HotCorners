using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SmartHotCorners
{
    class Program
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        const int WH_MOUSE_LL = 14;
        const int WM_MOUSEMOVE = 0x0200;

        const int KEYEVENTF_KEYUP = 0x0002;
        const byte VK_LWIN = 0x5B;
        const byte VK_TAB = 0x09;

#pragma warning disable 0649
        public struct POINT
        {
            public int x;
            public int y;
        }
#pragma warning restore 0649

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static IntPtr hookId = IntPtr.Zero;
        private static LowLevelMouseProc hookProc;
        private static string logPath;
        private static string virtualDesktopPath;

        // State tracking
        private static bool inTopLeft = false;
        private static bool inBottomLeft = false;
        private static bool inBottomRight = false;
        private static long lastTriggerTime = 0;

        static void Main(string[] args)
        {
            // Enable Per-Monitor DPI Awareness so screen metrics and hook coordinates align 1:1
            SetProcessDPIAware();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(baseDir, "HotCorners.log");

            // Resolve VirtualDesktop.exe path dynamically
            virtualDesktopPath = Path.Combine(baseDir, "VirtualDesktop.exe");
            if (!File.Exists(virtualDesktopPath))
            {
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsHotCorners", "VirtualDesktop.exe");
                if (File.Exists(appDataDir))
                {
                    virtualDesktopPath = appDataDir;
                }
                else if (File.Exists(@"E:\Windows Configurations\VirtualDesktop.exe"))
                {
                    virtualDesktopPath = @"E:\Windows Configurations\VirtualDesktop.exe";
                }
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log("FATAL UNHANDLED: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null"));
            };

            try
            {
                Log("Starting Smart Hot Corners (v2.0 Zero-Taskbar Engine)...");

                Thread worker = new Thread(() =>
                {
                    try
                    {
                        // Attach to active user desktop
                        IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                        bool deskSet = SetThreadDesktop(hDesk);
                        Log(string.Format("Worker thread desktop set: {0}", deskSet));

                        int w = GetSystemMetrics(0); // Screen width (physical pixels)
                        int h = GetSystemMetrics(1); // Screen height (physical pixels)

                        const int CORNER_W = 90;        // Corner width in physical pixels
                        const int CORNER_H = 80;        // Corner height in physical pixels
                        const int EXIT_DIST = 140;      // Distance to re-arm corner trigger
                        const int COOLDOWN_MS = 400;    // Milliseconds between switches
                        int safeY = h - 6;              // Barrier 6px above bottom edge (taskbar sensor is at h - 2)

                        Log(string.Format("Display: {0}x{1}. Corner Box: {2}x{3}px. Safe Barrier: Y={4}", w, h, CORNER_W, CORNER_H, safeY));

                        hookProc = (nCode, wParam, lParam) =>
                        {
                            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
                            {
                                MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                                int curX = ms.pt.x;
                                int curY = ms.pt.y;
                                long now = Environment.TickCount;

                                // 1. TOP-LEFT: Task View (Win + Tab)
                                if (curX <= CORNER_W && curY <= CORNER_H)
                                {
                                    if (!inTopLeft && (now - lastTriggerTime >= COOLDOWN_MS))
                                    {
                                        inTopLeft = true;
                                        lastTriggerTime = now;
                                        Log(string.Format("Top-Left flick at ({0},{1}). Triggering Win+Tab.", curX, curY));
                                        TriggerWinTab();
                                    }
                                }
                                else if (curX > EXIT_DIST || curY > EXIT_DIST)
                                {
                                    inTopLeft = false;
                                }

                                // 2. BOTTOM-LEFT: Virtual Desktop Left (Ctrl + Win + Left)
                                bool isBL = (curX <= CORNER_W && curY >= (h - CORNER_H));
                                if (isBL)
                                {
                                    if (!inBottomLeft && (now - lastTriggerTime >= COOLDOWN_MS))
                                    {
                                        inBottomLeft = true;
                                        lastTriggerTime = now;
                                        Log(string.Format("Bottom-Left flick at ({0},{1}). Triggering Desktop Left.", curX, curY));
                                        SwitchDesktopLeft();
                                    }

                                    // CORNER TASKBAR SHIELD: Clamp cursor above taskbar sensor (h - 2)
                                    // and swallow the raw mouse event so Windows Explorer NEVER sees mouse in the corner!
                                    if (curY >= safeY)
                                    {
                                        SetCursorPos(curX, safeY);
                                        return (IntPtr)1; // Swallow event
                                    }
                                }
                                else if (curX > EXIT_DIST || curY < (h - EXIT_DIST))
                                {
                                    inBottomLeft = false;
                                }

                                // 3. BOTTOM-RIGHT: Virtual Desktop Right (Ctrl + Win + Right)
                                bool isBR = (curX >= (w - CORNER_W) && curY >= (h - CORNER_H));
                                if (isBR)
                                {
                                    if (!inBottomRight && (now - lastTriggerTime >= COOLDOWN_MS))
                                    {
                                        inBottomRight = true;
                                        lastTriggerTime = now;
                                        Log(string.Format("Bottom-Right flick at ({0},{1}). Triggering Desktop Right.", curX, curY));
                                        SwitchDesktopRight();
                                    }

                                    // CORNER TASKBAR SHIELD: Clamp cursor above taskbar sensor (h - 2)
                                    // and swallow the raw mouse event so Windows Explorer NEVER sees mouse in the corner!
                                    if (curY >= safeY)
                                    {
                                        SetCursorPos(curX, safeY);
                                        return (IntPtr)1; // Swallow event
                                    }
                                }
                                else if (curX < (w - EXIT_DIST) || curY < (h - EXIT_DIST))
                                {
                                    inBottomRight = false;
                                }
                            }

                            // Pass through all center mouse movements (up-to-down unhides taskbar normally)
                            return CallNextHookEx(hookId, nCode, wParam, lParam);
                        };

                        using (Process curProcess = Process.GetCurrentProcess())
                        using (ProcessModule curModule = curProcess.MainModule)
                        {
                            hookId = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(curModule.ModuleName), 0);
                        }

                        Log(string.Format("Low-level mouse hook installed: {0}", hookId != IntPtr.Zero));

                        // Message pump processes mouse hook events with 0ms latency
                        Application.Run();
                    }
                    catch (Exception ex)
                    {
                        Log("Worker thread exception: " + ex.ToString());
                    }
                });

                worker.IsBackground = false; // Foreground thread keeps process alive permanently
                worker.Start();
                Log("Worker thread launched (foreground).");

                worker.Join();
            }
            catch (Exception ex)
            {
                Log("Main method exception: " + ex.ToString());
            }
        }

        private static void TriggerWinTab()
        {
            ThreadPool.QueueUserWorkItem((state) =>
            {
                try
                {
                    IntPtr fgHwnd = GetForegroundWindow();
                    uint fgThread = GetWindowThreadProcessId(fgHwnd, IntPtr.Zero);
                    uint curThread = GetCurrentThreadId();

                    if (fgThread != 0 && fgThread != curThread)
                    {
                        AttachThreadInput(curThread, fgThread, true);
                    }

                    keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(60);
                    keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    if (fgThread != 0 && fgThread != curThread)
                    {
                        AttachThreadInput(curThread, fgThread, false);
                    }
                }
                catch (Exception ex)
                {
                    Log("Error in TriggerWinTab: " + ex.Message);
                }
            });
        }

        private static void SwitchDesktopLeft()
        {
            RunVD("/Quiet /Left /Wrap");
        }

        private static void SwitchDesktopRight()
        {
            RunVD("/Quiet /Right /Wrap");
        }

        private static void RunVD(string arguments)
        {
            ThreadPool.QueueUserWorkItem((state) =>
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = virtualDesktopPath;
                    psi.Arguments = arguments;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    Log("Error in RunVD: " + ex.Message);
                }
            });
        }

        private static void Log(string message)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, message);
                Console.WriteLine(line);
                if (!string.IsNullOrEmpty(logPath))
                {
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
