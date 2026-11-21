using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace reactivo.Classes
{
    [SuppressUnmanagedCodeSecurity]
    public static class ConsoleManager
    {
        private static readonly object _logLock = new();
        private static readonly string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "reactivo"
        );
        private static readonly string _logPath = Path.Combine(_logDir, "reactivo.log");
        private static readonly string _backupLogPath = Path.Combine(_logDir, "reactivo.log.1");
        private const long MaxLogBytes = 256 * 1024;

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public static bool HasConsole() => GetConsoleWindow() != IntPtr.Zero;
        public static void Show() => AllocConsole();
        public static void Hide() => FreeConsole();
        public static void Toggle() { if (HasConsole()) Hide(); else Show(); }
        public static void ToFront() => SetForegroundWindow(GetConsoleWindow());

        public static void Log(string message)
        {
            if (!HasConsole()) return;
            try
            {
                Console.WriteLine(message);
            }
            catch
            {
                // Ignore IOException if console handle is detached or invalid
            }
        }

        public static void LogEvent(string message) => WriteFileLog("INFO", message);

        public static void LogError(string message) => WriteFileLog("ERROR", message);

        private static void WriteFileLog(string level, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            Log(line.TrimEnd());

            lock (_logLock)
            {
                try
                {
                    if (!Directory.Exists(_logDir))
                    {
                        Directory.CreateDirectory(_logDir);
                    }

                    if (File.Exists(_logPath))
                    {
                        var info = new FileInfo(_logPath);
                        if (info.Length >= MaxLogBytes)
                        {
                            File.Move(_logPath, _backupLogPath, overwrite: true);
                        }
                    }

                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
                catch
                {
                    // Logging must never crash the audio/WebSocket runtime
                }
            }
        }
    }
}
