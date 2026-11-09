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
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenDesktop(string s, uint f, bool i, uint a);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr d);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc fn, IntPtr mod, uint thId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern IntPtr GetModuleHandle(string mod);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
        [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

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
            SetProcessDPIAware();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(baseDir, "HotCorners.log");

            virtualDesktopPath = Path.Combine(baseDir, "VirtualDesktop.exe");
            if (!File.Exists(virtualDesktopPath))
            {
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsHotCorners", "VirtualDesktop.exe");
                if (File.Exists(appDataDir)) virtualDesktopPath = appDataDir;
                else if (File.Exists(@"E:\Windows Configurations\VirtualDesktop.exe")) virtualDesktopPath = @"E:\Windows Configurations\VirtualDesktop.exe";
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log("FATAL UNHANDLED: " + (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "null"));
            };

            try
            {
                Log("Starting Smart Hot Corners (Corner-Immune Engine v3.0)...");

                Thread worker = new Thread(() =>
                {
                    try
                    {
                        IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                        bool deskSet = SetThreadDesktop(hDesk);
                        Log(string.Format("Worker thread desktop set: {0}", deskSet));

                        int w = GetSystemMetrics(0); // Screen width
                        int h = GetSystemMetrics(1); // Screen height

                        const int CORNER_TRIGGER_W = 160; // 160px corner trigger width
                        const int CORNER_TRIGGER_H = 100; // 100px corner trigger height
                        const int CORNER_SHIELD_W = 250;  // 250px taskbar immunity zone in corners
                        const int EXIT_DIST = 180;        // Distance to re-arm corner trigger
                        const int COOLDOWN_MS = 400;      // Milliseconds between switches
                        int sensorY = h - 15;             // Safe barrier (blocks bottom 15px in corners)

                        Log(string.Format("Metrics: {0}x{1}. Trigger Box: {2}x{3}px. Shield Wing: {4}px. Sensor Barrier: Y={5}",
                            w, h, CORNER_TRIGGER_W, CORNER_TRIGGER_H, CORNER_SHIELD_W, sensorY));

                        hookProc = (nCode, wParam, lParam) =>
                        {
                            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE)
                            {
                                MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                                int curX = ms.pt.x;
                                int curY = ms.pt.y;
                                long now = Environment.TickCount;

                                // 1. TOP-LEFT CORNER: Task View (Win + Tab)
                                if (curX <= CORNER_TRIGGER_W && curY <= CORNER_TRIGGER_H)
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

                                // 2. BOTTOM-LEFT CORNER: Previous Virtual Desktop (Left)
                                bool isBL = (curX <= CORNER_TRIGGER_W && curY >= (h - CORNER_TRIGGER_H));
                                if (isBL)
                                {
                                    if (!inBottomLeft && (now - lastTriggerTime >= COOLDOWN_MS))
                                    {
                                        inBottomLeft = true;
                                        lastTriggerTime = now;
                                        Log(string.Format("Bottom-Left flick at ({0},{1}). Triggering Desktop Left.", curX, curY));
                                        SwitchDesktopLeft();
                                    }
                                }
                                else if (curX > EXIT_DIST || curY < (h - EXIT_DIST))
                                {
                                    inBottomLeft = false;
                                }

                                // 3. BOTTOM-RIGHT CORNER: Next Virtual Desktop (Right)
                                bool isBR = (curX >= (w - CORNER_TRIGGER_W) && curY >= (h - CORNER_TRIGGER_H));
                                if (isBR)
                                {
                                    if (!inBottomRight && (now - lastTriggerTime >= COOLDOWN_MS))
                                    {
                                        inBottomRight = true;
                                        lastTriggerTime = now;
                                        Log(string.Format("Bottom-Right flick at ({0},{1}). Triggering Desktop Right.", curX, curY));
                                        SwitchDesktopRight();
                                    }
                                }
                                else if (curX < (w - EXIT_DIST) || curY < (h - EXIT_DIST))
                                {
                                    inBottomRight = false;
                                }

                                // 4. CORNER TASKBAR IMMUNITY SHIELD:
                                // In the bottom-left or bottom-right corners:
                                // If the cursor tries to touch the bottom edge sensor line (curY >= sensorY),
                                // SWALLOW the raw event directly. The taskbar NEVER receives mouse input in corners!
                                if ((curX <= CORNER_SHIELD_W || curX >= (w - CORNER_SHIELD_W)) && curY >= sensorY)
                                {
                                    return (IntPtr)1; // Pure swallow: 0ms latency, zero driver fight
                                }

                                // 5. CENTER ZONE: COMPLETELY UNTOUCHED!
                                // Moving mouse to the bottom in the center unhides the taskbar immediately and normally.
                            }

                            return CallNextHookEx(hookId, nCode, wParam, lParam);
                        };

                        using (Process curProcess = Process.GetCurrentProcess())
                        using (ProcessModule curModule = curProcess.MainModule)
                        {
                            hookId = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(curModule.ModuleName), 0);
                        }

                        Log(string.Format("Low-level mouse hook active: {0}", hookId != IntPtr.Zero));

                        Application.Run();
                    }
                    catch (Exception ex)
                    {
                        Log("Worker thread exception: " + ex.ToString());
                    }
                });

                worker.IsBackground = false;
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

                    if (fgThread != 0 && fgThread != curThread) AttachThreadInput(curThread, fgThread, true);

                    keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(60);
                    keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    if (fgThread != 0 && fgThread != curThread) AttachThreadInput(curThread, fgThread, false);
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
            ThreadPool.QueueUserWorkItem((state) =>
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
            });
        }
    }
}
