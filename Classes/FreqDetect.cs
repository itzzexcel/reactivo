using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace reactivo.Classes;

public sealed class AudioAnalysisPayload
{
    [JsonPropertyName("utime")]
    public long utime { get; set; }

    [JsonPropertyName("analysis")]
    public int analysis { get; set; }

    [JsonPropertyName("energy")]
    public float energy { get; set; }

    [JsonPropertyName("bass")]
    public FrequencyBand bass { get; set; } = new();

    [JsonPropertyName("treble")]
    public FrequencyBand treble { get; set; } = new();

    [JsonPropertyName("bpm")]
    public float bpm { get; set; }
}

public sealed class FrequencyBand
{
    [JsonPropertyName("average")]
    public float average { get; set; }

    [JsonPropertyName("max")]
    public float max { get; set; }

    [JsonPropertyName("strongest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StrongestFrequency? strongest { get; set; }
}

public sealed class StrongestFrequency
{
    [JsonPropertyName("frequency")]
    public float frequency { get; set; }

    [JsonPropertyName("magnitude")]
    public float magnitude { get; set; }
}

public class FrequencyDetector
{
    public WasapiLoopbackCapture? _capture;
    private readonly int _bufferSize = 2048;
    private readonly int _hopSize = 1024; // 50% overlap: ~21.3ms update interval, pure 23.4Hz bass resolution
    private readonly Complex[] _fftBuffer;
    private readonly float[] _audioBuffer;
    private readonly float[] _hammingWindow;
    private int _audioBufferPosition = 0;
    private AudioDeviceNotifier? _deviceNotifier;
    private MMDevice? _currentDevice;

    // Thresholds
    private const float BassThreshold = 0.001f;
    private const float TrebleThreshold = 0.001f;

    // Frequency ranges (Hz)
    private const int BassMaxFreq = 200;
    private const int TrebleMinFreq = 4000;

    private int _analysisCounter = 0;
    private float _maxBassLevel = 0f;
    private float _maxTrebleLevel = 0f;
    private readonly BPMDetect _beatDetector;
    private float _lastBPM = 0f;
    private DateTime _lastBPMUpdate = DateTime.MinValue;
    public bool _isRunning = false;

    // Preallocated, zero-allocation serialization structures
    private readonly AudioAnalysisPayload _payload = new();
    private readonly AudioAnalysisPayload[] _payloadArray;
    private readonly StrongestFrequency _strongestBass = new();
    private readonly StrongestFrequency _strongestTreble = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Error codes for Exclusive Mode
    private const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    private const int AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED = unchecked((int)0x88890008);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    public FrequencyDetector()
    {
        _fftBuffer = new Complex[_bufferSize];
        _audioBuffer = new float[_bufferSize];
        _hammingWindow = new float[_bufferSize];
        _beatDetector = new BPMDetect();

        // Precompute Hamming window coefficients once (eliminates 2048 Math.Cos calls per frame)
        for (int i = 0; i < _bufferSize; i++)
        {
            _hammingWindow[i] = (float)(0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (_bufferSize - 1)));
        }

        _payloadArray = new[] { _payload };
    }

    public void StartMonitoring()
    {
        try
        {
            _isRunning = true;

            // Setup device change notifications
            _deviceNotifier = new AudioDeviceNotifier();
            _deviceNotifier.DefaultDeviceChanged += OnDefaultDeviceChanged;

            // Start capture with current device
            InitializeCapture();
        }
        catch (Exception ex)
        {
            ConsoleManager.Log($"Error starting monitoring:\n{ex.Message}\n{ex.StackTrace}\n{ex.InnerException}");
            Globals.Announce($"Audio Error\n\n{ex.Message}\n{ex.StackTrace}\n{ex.InnerException}");
        }
    }

    public void InitializeCapture()
    {
        var enumerator = new MMDeviceEnumerator();

        try
        {
            if (string.IsNullOrEmpty(Globals.tidalReceivedDevice) || Globals.tidalReceivedDevice == "default")
            {
                _currentDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            else
            {
                bool deviceExists = false;
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (device.ID == Globals.tidalReceivedDevice)
                    {
                        _currentDevice = device;
                        deviceExists = true;
                        ConsoleManager.Log($"Device found!: {device.FriendlyName}");
                        break;
                    }
                }

                if (!deviceExists)
                {
                    ConsoleManager.Log($"Device not found: {Globals.tidalReceivedDevice}. Using default device.");
                    _currentDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleManager.Log($"Couldn't get device: {ex.Message}");
            _currentDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        try
        {
            ConsoleManager.Log($"Monitoring audio from: {_currentDevice!.FriendlyName} // {_currentDevice.ID}");

            _capture = new WasapiLoopbackCapture(_currentDevice);
            _capture.DataAvailable += OnDataAvailable!;
            _capture.RecordingStopped += OnRecordingStopped!;

            ConsoleManager.Log($"Sample Rate: {_capture.WaveFormat.SampleRate} Hz");
            ConsoleManager.Log($"Channels: {_capture.WaveFormat.Channels}");
            ConsoleManager.Log($"Bits per Sample: {_capture.WaveFormat.BitsPerSample}");

            _capture.StartRecording();

            ConsoleManager.Log("Audio monitoring started.");
            ConsoleManager.Log("Monitoring: Bass (20-200 Hz), Treble (4000-20000 Hz), and Tempo (BPM)");
        }
        catch (COMException comEx)
        {
            HandleCOMException(comEx);
        }
        catch (Exception ex)
        {
            ConsoleManager.Log($"Error initializing capture: {ex.Message}");
            throw;
        }
    }

    private void HandleCOMException(COMException comEx)
    {
        string message;

        switch (comEx.HResult)
        {
            case AUDCLNT_E_DEVICE_IN_USE:
                message = "The audio device is being used exclusively by another application.\n\n" +
                          "Please close other audio applications or disable exclusive mode in:\n" +
                          "Windows Settings > Sound > Device Properties > Advanced Options";
                ConsoleManager.Log("ERROR: Device in exclusive use (AUDCLNT_E_DEVICE_IN_USE)");
                break;

            case AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED:
                message = "Exclusive mode is not allowed for this device.\n\n" +
                          "Try disabling exclusive mode in Windows settings.";
                ConsoleManager.Log("ERROR: Exclusive mode not allowed (AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED)");
                break;

            case AUDCLNT_E_DEVICE_INVALIDATED:
                message = "The audio device has been disconnected or is invalid.\n\n" +
                          "Verify that the device is properly connected.";
                ConsoleManager.Log("ERROR: Device invalidated (AUDCLNT_E_DEVICE_INVALIDATED)");
                break;

            default:
                message = $"Error accessing audio device (Code: 0x{comEx.HResult:X8}):\n\n{comEx.Message}";
                ConsoleManager.Log($"COM ERROR: {comEx.HResult:X8} - {comEx.Message}");
                break;
        }

        Globals.Announce($"Audio Error\n\n{message}");
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
    {
        if (!_isRunning) return;

        ConsoleManager.Log("Default device change detected. Restarting capture...");

        Task.Run(() =>
        {
            try
            {
                _capture?.StopRecording();
                _capture?.Dispose();
                _capture = null;

                Thread.Sleep(300);
                InitializeCapture();
            }
            catch (Exception ex)
            {
                ConsoleManager.Log($"Error switching device: {ex.Message}");
            }
        });
    }

    public void StopMonitoring()
    {
        _isRunning = false;
        _capture?.StopRecording();
        _capture?.Dispose();
        _deviceNotifier?.Dispose();
        _currentDevice?.Dispose();
    }

    private readonly object _audioLock = new();

    private void OnDataAvailable(object sender, WaveInEventArgs e)
    {
        lock (_audioLock)
        {
            try
            {
                if (_capture == null) return;

                int bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
                int channels = _capture.WaveFormat.Channels;
                int bytesPerFrame = bytesPerSample * channels;
                int framesAvailable = e.BytesRecorded / bytesPerFrame;
                bool isFloat32 = _capture.WaveFormat.BitsPerSample == 32;
                bool isPcm16 = _capture.WaveFormat.BitsPerSample == 16;

                for (int frame = 0; frame < framesAvailable; frame++)
                {
                    int byteOffset = frame * bytesPerFrame;
                    if (byteOffset + bytesPerFrame > e.BytesRecorded)
                        break;

                    float sample = 0f;

                    // Downmix stereo (Left + Right) so hard-panned instruments/transients are accurately captured
                    if (isFloat32)
                    {
                        if (channels >= 2)
                        {
                            float left = BitConverter.ToSingle(e.Buffer, byteOffset);
                            float right = BitConverter.ToSingle(e.Buffer, byteOffset + 4);
                            sample = (left + right) * 0.5f;
                        }
                        else
                        {
                            sample = BitConverter.ToSingle(e.Buffer, byteOffset);
                        }
                    }
                    else if (isPcm16)
                    {
                        if (channels >= 2)
                        {
                            float left = BitConverter.ToInt16(e.Buffer, byteOffset) / 32768.0f;
                            float right = BitConverter.ToInt16(e.Buffer, byteOffset + 2) / 32768.0f;
                            sample = (left + right) * 0.5f;
                        }
                        else
                        {
                            sample = BitConverter.ToInt16(e.Buffer, byteOffset) / 32768.0f;
                        }
                    }
                    else
                    {
                        continue;
                    }

                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                        sample = 0f;

                    _audioBuffer[_audioBufferPosition++] = sample;

                    if (_audioBufferPosition == _bufferSize)
                    {
                        PerformAnalysis();

                        // Sliding window: shift buffer by hop size (512 samples) to maintain 75% overlap
                        int remaining = _bufferSize - _hopSize;
                        Array.Copy(_audioBuffer, _hopSize, _audioBuffer, 0, remaining);
                        _audioBufferPosition = remaining;
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleManager.Log($"Error in OnDataAvailable: {ex.Message}");
                _audioBufferPosition = 0;
            }
        }
    }

    private void PerformAnalysis()
    {
        try
        {
            _analysisCounter++;

            // Combined Energy calculation and Hamming window application (1 single loop)
            float energySum = 0f;
            for (int i = 0; i < _bufferSize; i++)
            {
                float s = _audioBuffer[i];
                energySum += s * s;
                _fftBuffer[i].X = s * _hammingWindow[i];
                _fftBuffer[i].Y = 0f;
            }
            float energy = (float)Math.Sqrt(energySum / _bufferSize);

            // Beat detection
            _beatDetector.DetectBeat(energy);

            // Update BPM every second
            var now = DateTime.Now;
            if ((now - _lastBPMUpdate).TotalSeconds >= 1.0)
            {
                _lastBPM = _beatDetector.GetCurrentBPM();
                _lastBPMUpdate = now;
            }

            // Perform FFT
            int fftLength = (int)Math.Log(_bufferSize, 2.0);
            FastFourierTransform.FFT(true, fftLength, _fftBuffer);

            // Frequency Content Analysis:
            // Rather than scanning all 1024 bins, compute sample-rate dependent bin bounds
            // and scan ONLY the monitored ranges (Bass: 20-200Hz, Treble: 4000-20000Hz).
            // This skips ~160 middle bins, avoiding hundreds of Math.Sqrt calculations per frame!
            int sampleRate = _capture?.WaveFormat.SampleRate ?? 48000;
            float binWidth = (float)sampleRate / _bufferSize;
            float invBufferSize = 1.0f / _bufferSize;

            int minBassBin = Math.Max(1, (int)(20 / binWidth));
            int maxBassBin = Math.Min(_bufferSize / 2 - 1, (int)(BassMaxFreq / binWidth));

            int minTrebleBin = Math.Max(1, (int)(TrebleMinFreq / binWidth));
            int maxTrebleBin = Math.Min(_bufferSize / 2 - 1, (int)(20000 / binWidth));

            float bassSum = 0f;
            float trebleSum = 0f;
            int bassCount = 0;
            int trebleCount = 0;
            float maxBassFreq = 0f;
            float maxTrebleFreq = 0f;
            float maxBassMag = 0f;
            float maxTrebleMag = 0f;

            // 1. Bass band scan
            for (int i = minBassBin; i <= maxBassBin; i++)
            {
                float magSq = _fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y;
                float mag = (float)Math.Sqrt(magSq) * invBufferSize;
                bassSum += mag;
                bassCount++;
                if (mag > maxBassMag)
                {
                    maxBassMag = mag;
                    maxBassFreq = i * binWidth;
                }
            }

            // 2. Treble band scan
            for (int i = minTrebleBin; i <= maxTrebleBin; i++)
            {
                float magSq = _fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y;
                float mag = (float)Math.Sqrt(magSq) * invBufferSize;
                trebleSum += mag;
                trebleCount++;
                if (mag > maxTrebleMag)
                {
                    maxTrebleMag = mag;
                    maxTrebleFreq = i * binWidth;
                }
            }

            float bassAverage = bassCount > 0 ? bassSum / bassCount : 0f;
            float trebleAverage = trebleCount > 0 ? trebleSum / trebleCount : 0f;

            if (bassAverage > _maxBassLevel) _maxBassLevel = bassAverage;
            if (trebleAverage > _maxTrebleLevel) _maxTrebleLevel = trebleAverage;

            // If no clients are connected to WebSocket, skip serialization and broadcasting!
            // Saves CPU, allocations, and battery when Tidal is idle or visualizer is closed.
            if (!Globals.webSocket.HasConnectedClients)
            {
                return;
            }

            // Populate reusable DTO without heap allocations
            _payload.utime = DateTime.UtcNow.Ticks;
            _payload.analysis = _analysisCounter;
            _payload.energy = float.IsFinite(energy) ? energy : 0f;

            _payload.bass.average = float.IsFinite(bassAverage) ? bassAverage : 0f;
            _payload.bass.max = float.IsFinite(_maxBassLevel) ? _maxBassLevel : 0f;
            if (maxBassMag > 0f && float.IsFinite(maxBassFreq) && float.IsFinite(maxBassMag))
            {
                _strongestBass.frequency = maxBassFreq;
                _strongestBass.magnitude = maxBassMag;
                _payload.bass.strongest = _strongestBass;
            }
            else
            {
                _payload.bass.strongest = null;
            }

            _payload.treble.average = float.IsFinite(trebleAverage) ? trebleAverage : 0f;
            _payload.treble.max = float.IsFinite(_maxTrebleLevel) ? _maxTrebleLevel : 0f;
            if (maxTrebleMag > 0f && float.IsFinite(maxTrebleFreq) && float.IsFinite(maxTrebleMag))
            {
                _strongestTreble.frequency = maxTrebleFreq;
                _strongestTreble.magnitude = maxTrebleMag;
                _payload.treble.strongest = _strongestTreble;
            }
            else
            {
                _payload.treble.strongest = null;
            }

            _payload.bpm = float.IsFinite(_lastBPM) ? _lastBPM : 0f;

            // Serialize directly to compact UTF-8 byte array using cached options
            byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(_payloadArray, _jsonOptions);
            _ = Globals.webSocket.BroadcastBytesAsync(utf8Bytes);

            // Log to console only if console window is open (without JToken.Parse allocation)
            if (ConsoleManager.HasConsole())
            {
                string jsonString = System.Text.Encoding.UTF8.GetString(utf8Bytes);
                ConsoleManager.Log(jsonString);
            }
        }
        catch (Exception ex)
        {
            ConsoleManager.Log($"Error in PerformAnalysis: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            if (e.Exception is COMException comEx)
            {
                HandleCOMException(comEx);
            }
            else
            {
                ConsoleManager.Log($"Recording stopped due to error: {e.Exception.Message}");
            }
        }
        else
        {
            ConsoleManager.Log("Recording stopped.");
        }
    }
}
