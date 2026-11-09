using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SmartHotCorners
{
    class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out POINT lpPoint);

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

        const int KEYEVENTF_KEYUP = 0x0002;
        const byte VK_LWIN = 0x5B;
        const byte VK_TAB = 0x09;

        private static string logPath;
        private static string virtualDesktopPath;

        static void Main(string[] args)
        {
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
                Log("Starting Smart Hot Corners...");

                Thread worker = new Thread(() =>
                {
                    try
                    {
                        IntPtr hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                        bool set = SetThreadDesktop(hDesk);
                        Log(string.Format("Worker thread desktop set: {0}", set));

                        const int THRESHOLD = 40;       // Generous 40-pixel trigger area
                        const int EXIT_DISTANCE = 75;   // Hysteresis boundary to prevent repeat triggers
                        const int COOLDOWN_MS = 600;    // Milliseconds between triggers

                        bool inTopLeft = false;
                        bool inBottomLeft = false;
                        bool inBottomRight = false;
                        long lastTriggerTime = 0;

                        while (true)
                        {
                            Thread.Sleep(20);

                            POINT pt;
                            if (!GetCursorPos(out pt))
                            {
                                continue;
                            }

                            int w = GetSystemMetrics(0); // Screen width (DPI-scaled)
                            int h = GetSystemMetrics(1); // Screen height (DPI-scaled)
                            long now = Environment.TickCount;

                            // 1. TOP-LEFT: Windows + Tab
                            if (pt.X <= THRESHOLD && pt.Y <= THRESHOLD)
                            {
                                if (!inTopLeft && (now - lastTriggerTime >= COOLDOWN_MS))
                                {
                                    inTopLeft = true;
                                    lastTriggerTime = now;
                                    Log(string.Format("Top-Left flick at ({0},{1}). Triggering Win+Tab.", pt.X, pt.Y));
                                    TriggerWinTab();
                                }
                            }
                            else if (pt.X > EXIT_DISTANCE || pt.Y > EXIT_DISTANCE)
                            {
                                inTopLeft = false;
                            }

                            // 2. BOTTOM-LEFT: Virtual Desktop Left (Ctrl + Win + Left)
                            if (pt.X <= THRESHOLD && pt.Y >= (h - THRESHOLD))
                            {
                                if (!inBottomLeft && (now - lastTriggerTime >= COOLDOWN_MS))
                                {
                                    inBottomLeft = true;
                                    lastTriggerTime = now;
                                    Log(string.Format("Bottom-Left flick at ({0},{1}). Triggering Desktop Left.", pt.X, pt.Y));
                                    SwitchDesktopLeft();
                                    // Nudge cursor up above taskbar trigger zone (prevents taskbar popup at corner)
                                    SetCursorPos(pt.X, h - 55);
                                }
                                else if (inBottomLeft && pt.Y > (h - 55))
                                {
                                    // Keep cursor above taskbar while resting in corner
                                    SetCursorPos(pt.X, h - 55);
                                }
                            }
                            else if (pt.X > EXIT_DISTANCE || pt.Y < (h - EXIT_DISTANCE))
                            {
                                inBottomLeft = false;
                            }

                            // 3. BOTTOM-RIGHT: Virtual Desktop Right (Ctrl + Win + Right)
                            if (pt.X >= (w - THRESHOLD) && pt.Y >= (h - THRESHOLD))
                            {
                                if (!inBottomRight && (now - lastTriggerTime >= COOLDOWN_MS))
                                {
                                    inBottomRight = true;
                                    lastTriggerTime = now;
                                    Log(string.Format("Bottom-Right flick at ({0},{1}). Triggering Desktop Right.", pt.X, pt.Y));
                                    SwitchDesktopRight();
                                    // Nudge cursor up above taskbar trigger zone (prevents taskbar popup at corner)
                                    SetCursorPos(pt.X, h - 55);
                                }
                                else if (inBottomRight && pt.Y > (h - 55))
                                {
                                    // Keep cursor above taskbar while resting in corner
                                    SetCursorPos(pt.X, h - 55);
                                }
                            }
                            else if (pt.X < (w - EXIT_DISTANCE) || pt.Y < (h - EXIT_DISTANCE))
                            {
                                inBottomRight = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Worker thread exception: " + ex.ToString());
                    }
                });

                worker.IsBackground = false; // Foreground thread keeps process alive permanently
                worker.Start();
                Log("Worker thread launched (foreground).");

                // Keep main thread alive
                worker.Join();
            }
            catch (Exception ex)
            {
                Log("Main method exception: " + ex.ToString());
            }
        }

        private static void TriggerWinTab()
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
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = virtualDesktopPath;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                Log("Error in RunVD: " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine);
                File.AppendAllText(logPath, line);
            }
            catch { }
        }
    }
}

