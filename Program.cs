using reactivo.Classes;

namespace reactivo;

class Program
{
    private static bool _isRunning = true;
    private static DateTime _lastBassTime = DateTime.MinValue;
    private static DateTime _lastTrebleTime = DateTime.MinValue;
    private static DateTime _lastBeatTime = DateTime.MinValue;
    private static float _currentBPM = 0;

    static void Main(string[] args)
    {
        Console.WriteLine("\treactivo");
        Console.WriteLine("=================================");

        var detector = new FrequencyDetector();
        detector.FrequencyDetected += OnFrequencyDetected;

        try
        {
            detector.StartMonitoring();

            while (_isRunning)
            {
                var key = Console.ReadKey(true);
                if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                {
                    _isRunning = false;
                }
            }

            detector.StopMonitoring();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine("Make sure your audio device is working and try running as administrator.");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }

        Console.WriteLine("Application ended. Press any key to exit.");
        Console.ReadKey();
    }

    private static void OnFrequencyDetected(bool hasBass, bool hasTreble, float bassLevel, float trebleLevel, bool beatDetected, float currentBPM)
    {
        var now = DateTime.Now;
        var bassStatus = "";
        var trebleStatus = "";
        var beatStatus = "";

        if (hasBass && (now - _lastBassTime).TotalMilliseconds > 50)
        {
            bassStatus = $"[BASS: {bassLevel:F6}]";
            _lastBassTime = now;
        }

        if (hasTreble && (now - _lastTrebleTime).TotalMilliseconds > 50)
        {
            trebleStatus = $"[TREBLE: {trebleLevel:F6}]";
            _lastTrebleTime = now;
        }

        if (beatDetected && (now - _lastBeatTime).TotalMilliseconds > 100)
        {
            beatStatus = $"[BEAT] BPM: {currentBPM:F1}";
            _lastBeatTime = now;
            _currentBPM = currentBPM;
        }

        // Show BPM updates even without beats if tempo changed significantly
        if (Math.Abs(currentBPM - _currentBPM) > 5 && currentBPM > 0)
        {
            Console.WriteLine($"[BPM UPDATE: {currentBPM:F1}]");
            _currentBPM = currentBPM;
        }
    }
}