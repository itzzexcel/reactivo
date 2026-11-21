using Microsoft.Win32;
using reactivo.Classes;

namespace reactivo;

class Program
{
    static readonly FrequencyDetector detector = new();

    static async Task Main(string[] args)
    {
        using var instanceMutex = new Mutex(true, @"Local\reactivo_singleton_mutex", out bool createdNew);
        if (!createdNew)
        {
            ConsoleManager.LogEvent("Another reactivo instance is already running; exiting cleanly.");
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ConsoleManager.LogError($"Unhandled AppDomain exception (terminating={e.IsTerminating}): {ex}");
            }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            ConsoleManager.LogError($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        ConsoleManager.Hide();
        bool showConsole = false;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--show-console":
                    ConsoleManager.Show();
                    showConsole = true;
                    break;
                case "--register":
                    RegisterInStartup();
                    break;
            }
        }

        RegisterInStartup();
        ConsoleManager.LogEvent("reactivo starting up...");

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try { cts.Cancel(); } catch { }
        };

        // Supervise WebSocket server independently so transient faults restart automatically
        _ = RunSupervisedWebSocketAsync(cts.Token);

        try
        {
            detector.StartMonitoring();

            if (showConsole)
            {
                _ = Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(true);
                                if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                                {
                                    cts.Cancel();
                                    break;
                                }
                            }
                            await Task.Delay(100, cts.Token);
                        }
                        catch (OperationCanceledException) { break; }
                        catch { break; }
                    }
                }, cts.Token);
            }

            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            ConsoleManager.LogEvent("Shutting down...");
        }
        catch (Exception ex)
        {
            ConsoleManager.LogError($"Fatal runtime loop error: {ex}");
        }
        finally
        {
            detector.StopMonitoring();
            Globals.webSocket.StopServer();
            ConsoleManager.LogEvent("reactivo stopped cleanly.");
        }

        if (ConsoleManager.HasConsole() && showConsole)
        {
            ConsoleManager.Log("Application ended. Press any key to exit.");
            try { Console.ReadKey(); } catch { }
        }
    }

    private static async Task RunSupervisedWebSocketAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Globals.webSocket.StartServerAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsoleManager.LogError($"Supervised WebSocket wrapper caught exception: {ex.Message}");
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }
    }

    public static void DeviceChanged()
    {
        ConsoleManager.LogEvent($"Tidal active device updated to '{Globals.tidalReceivedDevice}'");
        if (!detector._isRunning) return;
        detector.RequestRecovery($"Tidal switched device to '{Globals.tidalReceivedDevice}'", 300);
    }

    private static void RegisterInStartup()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                ConsoleManager.LogEvent("Unable to determine executable path for startup registration.");
                return;
            }

            string appName = "reactivo";
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

            using (var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(runKey))
            {
                if (key == null)
                {
                    ConsoleManager.LogEvent("Failed to open or create registry key for startup registration.");
                    return;
                }

                key.SetValue(appName, $"\"{exePath}\"", RegistryValueKind.String);
            }

            ConsoleManager.LogEvent($"Registered '{appName}' to run at user logon.");
        }
        catch (Exception ex)
        {
            ConsoleManager.LogError($"Failed to register startup: {ex.Message}");
        }
    }
}
