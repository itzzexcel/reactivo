using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.InteropServices;

namespace reactivo.Classes;

public class FrequencyDetector
{
    public WasapiLoopbackCapture? _capture;
    private readonly int _bufferSize = 2048;
    private Complex[] _fftBuffer;
    private float[] _audioBuffer;
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
    private float _maxBassLevel = 0;
    private float _maxTrebleLevel = 0;
    private BPMDetect _beatDetector;
    private float _lastBPM = 0;
    private DateTime _lastBPMUpdate = DateTime.MinValue;
    public bool _isRunning = false;

    // Error codes for Exclusive Mode
    private const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    private const int AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED = unchecked((int)0x88890008);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    public FrequencyDetector()
    {
        _fftBuffer = new Complex[_bufferSize];
        _audioBuffer = new float[_bufferSize];
        _beatDetector = new BPMDetect();
    }

    public void StartMonitoring()
    {
        try
        {
            _isRunning = true;

            // Setup device change notifications
            _deviceNotifier = new AudioDeviceNotifier();

            // Start capture with current device
            InitializeCapture();
        }
        catch (Exception ex)
        {
            ConsoleManager.Log($"Error starting monitoring:\n{ex.Message}\n{ex.StackTrace}\n{ex.InnerException}");
            Globals.Announce($"{"Audio Error\n\n"}{ex.Message}\n{ex.StackTrace}\n{ex.InnerException}");
        }
    }

    public void InitializeCapture()
    {
        var enumerator = new MMDeviceEnumerator();

        if (string.IsNullOrEmpty(Globals.tidalReceivedDevice))
        {
            Console.WriteLine("Plugin is probably not running. Waiting it 67s...");
            int attempts = 0;
            while (string.IsNullOrEmpty(Globals.tidalReceivedDevice) && attempts < 67)
            {
                Thread.Sleep(1000);
                attempts++;
            }

            if (string.IsNullOrEmpty(Globals.tidalReceivedDevice))
            {
                ConsoleManager.Log("Timeouted. Changing to default playback device.");
                _currentDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
        }

        try
        {
            if (Globals.tidalReceivedDevice == "default")
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
                    ConsoleManager.Log($"Device not found: {Globals.tidalReceivedDevice}");
                    ConsoleManager.Log("Using default device.");
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
            var propertyStore = _currentDevice!.Properties;

            ConsoleManager.Log($"Monitoring audio from: {_currentDevice.FriendlyName} // {_currentDevice.ID}");

            _capture = new WasapiLoopbackCapture(_currentDevice);
            _capture.DataAvailable += OnDataAvailable!;
            _capture.RecordingStopped += OnRecordingStopped!;

            ConsoleManager.Log($"Sample Rate: {_capture.WaveFormat.SampleRate} Hz");
            ConsoleManager.Log($"Channels: {_capture.WaveFormat.Channels}");
            ConsoleManager.Log($"Bits per Sample: {_capture.WaveFormat.BitsPerSample}");

            _capture.StartRecording();

            ConsoleManager.Log("Audio monitoring started. Press 'q' to quit.");
            ConsoleManager.Log("Monitoring: Bass (20-250 Hz), Treble (4000-20000 Hz), and Tempo (BPM)");
            ConsoleManager.Log("Debug info will show every 50 analyses...\n");
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

        Globals.Announce($"{"Audio Error\n\n"}{message}");
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
    {
        if (!_isRunning) return;

        ConsoleManager.Log("Default device changed detected. Restarting capture...");

        Task.Run(() =>
        {
            try
            {
                // Stop current capture
                _capture?.StopRecording();
                _capture?.Dispose();
                _capture = null;

                // Small delay to ensure device is ready
                Thread.Sleep(500);

                // Restart with new device
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

                for (int frame = 0; frame < framesAvailable; frame++)
                {
                    int byteOffset = frame * bytesPerFrame;

                    if (byteOffset + bytesPerSample > e.BytesRecorded)
                        break;

                    float sample = 0f;

                    if (_capture.WaveFormat.BitsPerSample == 32)
                    {
                        sample = BitConverter.ToSingle(e.Buffer, byteOffset);
                    }
                    else if (_capture.WaveFormat.BitsPerSample == 16)
                    {
                        sample = BitConverter.ToInt16(e.Buffer, byteOffset) / 32768.0f;
                    }
                    else
                    {
                        continue;
                    }

                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                        sample = 0f;

                    if (_audioBufferPosition >= _audioBuffer.Length)
                    {
                        ConsoleManager.Log($"Audio buffer overflow. Position: {_audioBufferPosition}, Length: {_audioBuffer.Length}");
                        _audioBufferPosition = 0;
                    }

                    int bufferLen = _audioBuffer.Length;

                    _audioBuffer[_audioBufferPosition++] = sample;

                    if (_audioBufferPosition == bufferLen)
                    {
                        PerformAnalysis();
                        _audioBufferPosition = 0;
                    }

                }
            }
            catch (Exception ex)
            {
                ConsoleManager.Log($"Error in OnDataAvailable: {ex.Message}");
                ConsoleManager.Log($"Stack trace: {ex.StackTrace}");
                _audioBufferPosition = 0;
            }
        }
    }



    private void PerformAnalysis()
    {
        try
        {
            _analysisCounter++;

            // Calculate energy for beat detection (focus on low-mid frequencies)
            float energy = 0;
            for (int i = 0; i < _bufferSize; i++)
            {
                energy += _audioBuffer[i] * _audioBuffer[i];
            }
            energy = (float)Math.Sqrt(energy / _bufferSize);

            // Detect beats
            bool beatDetected = _beatDetector.DetectBeat(energy);

            // Update BPM every second
            var now = DateTime.Now;
            if ((now - _lastBPMUpdate).TotalSeconds >= 1.0)
            {
                _lastBPM = _beatDetector.GetCurrentBPM();
                _lastBPMUpdate = now;
            }

            // Copy audio data to FFT buffer
            for (int i = 0; i < _bufferSize; i++)
            {
                _fftBuffer[i].X = _audioBuffer[i];
                _fftBuffer[i].Y = 0;
            }

            // Apply Hamming window
            for (int i = 0; i < _bufferSize; i++)
            {
                double window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (_bufferSize - 1));
                _fftBuffer[i].X *= (float)window;
            }

            // Perform FFT
            int fftLength = (int)Math.Log(_bufferSize, 2.0);
            FastFourierTransform.FFT(true, fftLength, _fftBuffer);

            // Analyze frequency content
            bool hasBass = false;
            bool hasTreble = false;

            float bassSum = 0;
            float trebleSum = 0;
            int bassCount = 0;
            int trebleCount = 0;
            float maxBassFreq = 0;
            float maxTrebleFreq = 0;
            float maxBassMag = 0;
            float maxTrebleMag = 0;

            for (int i = 1; i < _bufferSize / 2; i++)
            {
                float frequency = (float)i * _capture!.WaveFormat.SampleRate / _bufferSize;
                float magnitude = (float)Math.Sqrt(_fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y);

                magnitude = magnitude / _bufferSize;

                if (frequency >= 20 && frequency <= BassMaxFreq)
                {
                    bassSum += magnitude;
                    bassCount++;
                    if (magnitude > maxBassMag)
                    {
                        maxBassMag = magnitude;
                        maxBassFreq = frequency;
                    }
                }
                else if (frequency >= TrebleMinFreq && frequency <= 20000)
                {
                    trebleSum += magnitude;
                    trebleCount++;
                    if (magnitude > maxTrebleMag)
                    {
                        maxTrebleMag = magnitude;
                        maxTrebleFreq = frequency;
                    }
                }
            }

            float bassAverage = bassCount > 0 ? bassSum / bassCount : 0;
            float trebleAverage = trebleCount > 0 ? trebleSum / trebleCount : 0;

            if (bassAverage > _maxBassLevel) _maxBassLevel = bassAverage;
            if (trebleAverage > _maxTrebleLevel) _maxTrebleLevel = trebleAverage;

            hasBass = bassAverage > BassThreshold;
            hasTreble = trebleAverage > TrebleThreshold;

            // Debug output every 'x' analyses
            if (_analysisCounter % 1 == 0)
            {
                if (true)
                {
                    var debugEntry = new
                    {
                        utime = DateTime.UtcNow.Ticks,
                        analysis = _analysisCounter,
                        energy = float.IsNaN(energy) || float.IsInfinity(energy) ? 0f : energy,
                        bass = new {
                            average = float.IsNaN(bassAverage) || float.IsInfinity(bassAverage) ? 0f : bassAverage,
                            max = float.IsNaN(_maxBassLevel) || float.IsInfinity(_maxBassLevel) ? 0f : _maxBassLevel,
                            strongest = maxBassMag > 0 && !float.IsNaN(maxBassFreq) && !float.IsInfinity(maxBassMag) ? new { frequency = maxBassFreq, magnitude = maxBassMag } : null
                        },
                        treble = new {
                            average = float.IsNaN(trebleAverage) || float.IsInfinity(trebleAverage) ? 0f : trebleAverage,
                            max = float.IsNaN(_maxTrebleLevel) || float.IsInfinity(_maxTrebleLevel) ? 0f : _maxTrebleLevel,
                            strongest = maxTrebleMag > 0 && !float.IsNaN(maxTrebleFreq) && !float.IsInfinity(maxTrebleMag) ? new { frequency = maxTrebleFreq, magnitude = maxTrebleMag } : null
                        },
                        bpm = float.IsNaN(_lastBPM) || float.IsInfinity(_lastBPM) ? 0f : _lastBPM
                    };


                    // Serialize with indentation, then convert the default space-based indentation
                    // into tab-based indentation with 4 tabs per level.
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    };
                    string jsonArray = System.Text.Json.JsonSerializer.Serialize(new[] { debugEntry }, options);
                    _ = Globals.webSocket.BroadcastMessage(jsonArray);
                    // Globals.namedPipe.Send(jsonArray);

                    ConsoleManager.Log(JToken.Parse(jsonArray).ToString(Formatting.Indented).ToString());
                }
            }

            // Trigger event
            // FrequencyDetected?.Invoke(hasBass, hasTreble, bassAverage, trebleAverage, beatDetected, _lastBPM);
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
