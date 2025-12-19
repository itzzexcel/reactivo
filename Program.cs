using Microsoft.Win32;
using reactivo.Classes;

namespace reactivo;

class Program
{

    static FrequencyDetector detector = new FrequencyDetector();

    static async Task Main(string[] args)
    {
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

        // IMPORTANT: If StartServerAsync returns a Task, store it
        var serverTask = Globals.webSocket.StartServerAsync();

        try
        {
            detector.StartMonitoring();

            // Create a cancellation token for clean shutdown
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                cts.Cancel();
            };

            // If console is visible, allow 'q' to quit
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

            // Keep running until cancellation
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
            ConsoleManager.Log("Shutting down...");
        }
        catch (Exception ex)
        {
            ConsoleManager.Log($"Error: {ex.Message}");
            ConsoleManager.Log("Make sure your audio device is working and try running as administrator.");
            ConsoleManager.Log($"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            detector.StopMonitoring();
        }

        if (ConsoleManager.HasConsole() && showConsole)
        {
            ConsoleManager.Log("Application ended. Press any key to exit.");
            Console.ReadKey();
        }
    }

    public static void DeviceChanged()
    {
        Console.WriteLine($"Device changed to {Globals.tidalReceivedDevice}");

        if (!detector._isRunning) return;

        ConsoleManager.Log("Default device changed detected. Restarting capture...");

        Task.Run(() =>
        {
            try
            {
                // Stop current capture
                detector._capture?.StopRecording();
                detector._capture?.Dispose();
                detector._capture = null;

                // Small delay to ensure device is ready
                Thread.Sleep(500);

                // Restart with new device
                detector.InitializeCapture();
            }
            catch (Exception ex)
            {
                ConsoleManager.Log($"Error switching device: {ex.Message}");
            }
        });
    }

    private static void RegisterInStartup()
    {
        try
        {
            string exePath = Environment.ProcessPath! ?? AppContext.BaseDirectory!;
            if (string.IsNullOrEmpty(exePath))
            {
                ConsoleManager.Log("Unable to determine executable path for startup registration.");
                return;
            }

            string appName = "reactivo";
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

            using (var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(runKey))
            {
                if (key == null)
                {
                    ConsoleManager.Log("Failed to open or create registry key for startup registration.");
                    return;
                }

                key.SetValue(appName, $"\"{exePath}\"", RegistryValueKind.String);
            }

            ConsoleManager.Log($"Registered '{appName}' to run at user logon.");
        }
        catch (Exception ex)
        {
            ConsoleManager.Log($"Failed to register startup: {ex.Message}");
        }
    }
}