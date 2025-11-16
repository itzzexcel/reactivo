using reactivo.Classes;
using System.Net.WebSockets;
using Microsoft.Win32;
using System.Reflection;

namespace reactivo;

class Program
{
    private static bool _isRunning = true;

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

        var detector = new FrequencyDetector();

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

    private static void OnFrequencyDetected(bool hasBass, bool hasTreble, float bassLevel, float trebleLevel, bool beatDetected, float currentBPM)
    {
        // Not updated / won't fix
        //var now = DateTime.Now;
        //var bassStatus = "";
        //var trebleStatus = "";
        //var beatStatus = "";

        //if (hasBass && (now - _lastBassTime).TotalMilliseconds > 50)
        //{
        //    bassStatus = $"[BASS: {bassLevel:F6}]";
        //    _lastBassTime = now;
        //}

        //if (hasTreble && (now - _lastTrebleTime).TotalMilliseconds > 50)
        //{
        //    trebleStatus = $"[TREBLE: {trebleLevel:F6}]";
        //    _lastTrebleTime = now;
        //}

        //if (beatDetected && (now - _lastBeatTime).TotalMilliseconds > 100)
        //{
        //    beatStatus = $"[BEAT] BPM: {currentBPM:F1}";
        //    _lastBeatTime = now;
        //    _currentBPM = currentBPM;
        //}

        //// Show BPM updates even without beats if tempo changed significantly
        //if (Math.Abs(currentBPM - _currentBPM) > 5 && currentBPM > 0)
        //{
        //    Console.WriteLine($"[BPM UPDATE: {currentBPM:F1}]");
        //    _currentBPM = currentBPM;
        //}
    }
}