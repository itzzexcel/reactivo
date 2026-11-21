using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace reactivo.Classes;

public sealed class AudioAnalysisPayload
{
    [JsonPropertyName("version")]
    public int version { get; set; } = 2;

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

    // Thresholds & Frequency ranges (Hz)
    private const int BassMaxFreq = 220;
    private const int TrebleMinFreq = 4000;

    private int _analysisCounter = 0;
    private float _maxBassLevel = 0f;
    private float _maxTrebleLevel = 0f;
    private readonly BPMDetect _beatDetector;
    private float _lastBPM = 0f;
    private DateTime _lastBPMUpdate = DateTime.MinValue;
    public volatile bool _isRunning = false;

    // Serialized self-healing supervisor state
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private CancellationTokenSource? _monitorCts;
    private Task? _supervisorTask;
    private volatile bool _recoveryRequested;
    private volatile int _recoveryDelayMs = 600;
    private int _consecutiveAnalysisErrors = 0;
    private DateTime _lastFailureLogUtc = DateTime.MinValue;

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

    // Error codes for Exclusive Mode & Device Invalidation
    private const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    private const int AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED = unchecked((int)0x88890008);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    private readonly object _audioLock = new();

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
        if (_isRunning) return;

        _isRunning = true;
        _monitorCts = new CancellationTokenSource();
        EnsureDeviceNotifier();

        // Perform an immediate non-throwing initial capture attempt
        _lifecycleLock.Wait();
        try
        {
            InitializeCaptureLocked();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        CancellationToken ct = _monitorCts.Token;
        _supervisorTask = Task.Run(() => RunSupervisorLoopAsync(ct), ct);
    }

    public void RequestRecovery(string reason, int delayMs = 600)
    {
        if (!_isRunning) return;

        ConsoleManager.LogEvent($"Recovery requested ({reason}, delay={delayMs}ms)");
        _recoveryDelayMs = Math.Max(100, delayMs);
        _recoveryRequested = true;

        try
        {
            if (_wakeSignal.CurrentCount == 0)
            {
                _wakeSignal.Release();
            }
        }
        catch { }
    }

    private void EnsureDeviceNotifier()
    {
        if (_deviceNotifier != null) return;
        try
        {
            var notifier = new AudioDeviceNotifier();
            notifier.DefaultDeviceChanged += OnDefaultDeviceChanged;
            notifier.DeviceStateChanged += OnDeviceStateChanged;
            notifier.DeviceAdded += OnDeviceAdded;
            notifier.DeviceRemoved += OnDeviceRemoved;
            _deviceNotifier = notifier;
        }
        catch (Exception ex)
        {
            LogRateLimitedError($"AudioDeviceNotifier init failed: {ex.Message}");
        }
    }

    private void DisposeDeviceNotifier()
    {
        var notifier = _deviceNotifier;
        if (notifier == null) return;
        _deviceNotifier = null;

        try
        {
            notifier.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            notifier.DeviceStateChanged -= OnDeviceStateChanged;
            notifier.DeviceAdded -= OnDeviceAdded;
            notifier.DeviceRemoved -= OnDeviceRemoved;
            notifier.Dispose();
        }
        catch { }
    }

    private void CleanupCaptureLocked()
    {
        var cap = _capture;
        _capture = null;

        if (cap != null)
        {
            try { cap.DataAvailable -= OnDataAvailable!; } catch { }
            try { cap.RecordingStopped -= OnRecordingStopped!; } catch { }
            try { cap.StopRecording(); } catch { }
            try { cap.Dispose(); } catch { }
        }

        var dev = _currentDevice;
        _currentDevice = null;
        if (dev != null)
        {
            try { dev.Dispose(); } catch { }
        }

        lock (_audioLock)
        {
            _audioBufferPosition = 0;
            _beatDetector.Reset();
        }
    }

    private bool InitializeCaptureLocked()
    {
        CleanupCaptureLocked();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? selectedDevice = null;

            string requestedId = Globals.tidalReceivedDevice;
            if (!string.IsNullOrEmpty(requestedId) && requestedId != "default")
            {
                try
                {
                    foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (selectedDevice == null && dev.ID == requestedId)
                        {
                            selectedDevice = dev;
                        }
                        else
                        {
                            dev.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogRateLimitedError($"EnumerateAudioEndPoints failed: {ex.Message}");
                }

                if (selectedDevice == null)
                {
                    ConsoleManager.LogEvent($"Requested device '{requestedId}' unavailable; falling back to default endpoint.");
                }
            }

            if (selectedDevice != null && TryStartCaptureOnDevice(selectedDevice))
            {
                return true;
            }

            selectedDevice?.Dispose();

            // Fallback to OS default multimedia render endpoint
            MMDevice? fallbackDevice = null;
            try
            {
                fallbackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (COMException comEx)
            {
                LogRateLimitedError($"No active default audio endpoint (0x{comEx.HResult:X8}): {comEx.Message}");
                return false;
            }
            catch (Exception ex)
            {
                LogRateLimitedError($"GetDefaultAudioEndpoint failed: {ex.Message}");
                return false;
            }

            if (fallbackDevice == null)
            {
                LogRateLimitedError("Default audio endpoint returned null.");
                return false;
            }

            if (TryStartCaptureOnDevice(fallbackDevice))
            {
                return true;
            }

            fallbackDevice.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            LogRateLimitedError($"InitializeCaptureLocked error: {ex.Message}");
            return false;
        }
    }

    private bool TryStartCaptureOnDevice(MMDevice device)
    {
        WasapiLoopbackCapture? cap = null;
        try
        {
            if (device.State != DeviceState.Active)
            {
                LogRateLimitedError($"Device '{device.FriendlyName}' is not active ({device.State}).");
                return false;
            }

            cap = new WasapiLoopbackCapture(device);
            cap.DataAvailable += OnDataAvailable!;
            cap.RecordingStopped += OnRecordingStopped!;
            cap.StartRecording();

            _currentDevice = device;
            _capture = cap;
            _consecutiveAnalysisErrors = 0;
            _maxBassLevel = 0f;
            _maxTrebleLevel = 0f;

            ConsoleManager.LogEvent(
                $"Capture active: {device.FriendlyName} ({cap.WaveFormat.SampleRate}Hz, {cap.WaveFormat.Channels}ch, {cap.WaveFormat.BitsPerSample}-bit)"
            );
            return true;
        }
        catch (COMException comEx)
        {
            if (cap != null)
            {
                try { cap.DataAvailable -= OnDataAvailable!; } catch { }
                try { cap.RecordingStopped -= OnRecordingStopped!; } catch { }
                try { cap.Dispose(); } catch { }
            }
            LogRateLimitedError(DescribeCOMException(comEx));
            return false;
        }
        catch (Exception ex)
        {
            if (cap != null)
            {
                try { cap.DataAvailable -= OnDataAvailable!; } catch { }
                try { cap.RecordingStopped -= OnRecordingStopped!; } catch { }
                try { cap.Dispose(); } catch { }
            }
            LogRateLimitedError($"Failed starting WASAPI capture on '{device.FriendlyName}': {ex.Message}");
            return false;
        }
    }

    private static string DescribeCOMException(COMException comEx) => comEx.HResult switch
    {
        AUDCLNT_E_DEVICE_IN_USE => "WASAPI device in exclusive use (AUDCLNT_E_DEVICE_IN_USE); will retry automatically.",
        AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED => "WASAPI exclusive mode not allowed (AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED); will retry.",
        AUDCLNT_E_DEVICE_INVALIDATED => "WASAPI device invalidated/unplugged (AUDCLNT_E_DEVICE_INVALIDATED); recovering.",
        _ => $"WASAPI COM error 0x{comEx.HResult:X8}: {comEx.Message}"
    };

    private static bool IsDeviceHealthy(MMDevice? device)
    {
        if (device == null) return false;
        try
        {
            return device.State == DeviceState.Active;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunSupervisorLoopAsync(CancellationToken ct)
    {
        int backoffMs = 1000;

        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                EnsureDeviceNotifier();

                bool needsRecovery = _recoveryRequested ||
                                     _capture == null ||
                                     _capture.CaptureState != CaptureState.Capturing ||
                                     !IsDeviceHealthy(_currentDevice) ||
                                     _consecutiveAnalysisErrors >= 10;

                if (needsRecovery)
                {
                    int delay = _recoveryRequested ? _recoveryDelayMs : 0;
                    _recoveryRequested = false;

                    if (delay > 0)
                    {
                        await Task.Delay(delay, ct);
                    }

                    // Coalesce burst events arriving right around the delay
                    _recoveryRequested = false;

                    bool recovered = false;
                    await _lifecycleLock.WaitAsync(ct);
                    try
                    {
                        if (_isRunning && !ct.IsCancellationRequested)
                        {
                            recovered = InitializeCaptureLocked();
                        }
                    }
                    finally
                    {
                        _lifecycleLock.Release();
                    }

                    if (recovered)
                    {
                        backoffMs = 1000;
                        continue;
                    }

                    // Retry with bounded backoff, waking early if a hardware/endpoint event arrives
                    await _wakeSignal.WaitAsync(backoffMs, ct);
                    backoffMs = Math.Min(10000, backoffMs * 2);
                    continue;
                }

                // Healthy state: sleep up to 3s and also detect OS sleep/hibernate clock jumps
                DateTime beforeWaitUtc = DateTime.UtcNow;
                await _wakeSignal.WaitAsync(3000, ct);
                double wallElapsedSec = (DateTime.UtcNow - beforeWaitUtc).TotalSeconds;
                if (wallElapsedSec > 12.0 && _isRunning && !ct.IsCancellationRequested)
                {
                    RequestRecovery($"System resume detected ({wallElapsedSec:F1}s wall jump)", 800);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRateLimitedError($"Supervisor loop error: {ex.Message}");
                try { await Task.Delay(2000, ct); } catch { break; }
            }
        }
    }

    private void LogRateLimitedError(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFailureLogUtc).TotalSeconds >= 10.0)
        {
            _lastFailureLogUtc = now;
            ConsoleManager.LogError(message);
        }
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId) =>
        RequestRecovery($"Default device changed ({deviceId})", 400);

    private void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (MatchesMonitoredDevice(deviceId) || _capture == null || _capture.CaptureState != CaptureState.Capturing)
        {
            RequestRecovery($"Device state changed ({deviceId} -> {newState})", 500);
        }
    }

    private void OnDeviceAdded(string deviceId)
    {
        if (_capture == null || _capture.CaptureState != CaptureState.Capturing || deviceId == Globals.tidalReceivedDevice)
        {
            RequestRecovery($"Device added ({deviceId})", 500);
        }
    }

    private void OnDeviceRemoved(string deviceId)
    {
        if (MatchesMonitoredDevice(deviceId) || _capture == null)
        {
            RequestRecovery($"Device removed ({deviceId})", 500);
        }
    }

    private bool MatchesMonitoredDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return true;
        if (!string.IsNullOrEmpty(Globals.tidalReceivedDevice) && deviceId == Globals.tidalReceivedDevice)
            return true;
        try
        {
            return _currentDevice != null && _currentDevice.ID == deviceId;
        }
        catch
        {
            return true;
        }
    }

    public void StopMonitoring()
    {
        _isRunning = false;

        try { _monitorCts?.Cancel(); } catch { }
        try { _supervisorTask?.Wait(1500); } catch { }

        if (_lifecycleLock.Wait(2000))
        {
            try
            {
                CleanupCaptureLocked();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        else
        {
            CleanupCaptureLocked();
        }

        DisposeDeviceNotifier();

        try { _monitorCts?.Dispose(); } catch { }
        _monitorCts = null;
        _supervisorTask = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_audioLock)
        {
            try
            {
                var capture = _capture;
                if (capture == null || !ReferenceEquals(sender, capture) || e.BytesRecorded <= 0)
                    return;

                int bitsPerSample = capture.WaveFormat.BitsPerSample;
                int bytesPerSample = bitsPerSample / 8;
                int channels = capture.WaveFormat.Channels;
                int bytesPerFrame = bytesPerSample * channels;
                if (bytesPerSample <= 0 || channels <= 0 || bytesPerFrame <= 0)
                    return;

                int framesAvailable = e.BytesRecorded / bytesPerFrame;
                bool isFloat32 = bitsPerSample == 32;
                bool isPcm16 = bitsPerSample == 16;

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

                    if (!float.IsFinite(sample))
                        sample = 0f;

                    _audioBuffer[_audioBufferPosition++] = sample;

                    if (_audioBufferPosition == _bufferSize)
                    {
                        PerformAnalysis(capture.WaveFormat.SampleRate);

                        int remaining = _bufferSize - _hopSize;
                        Array.Copy(_audioBuffer, _hopSize, _audioBuffer, 0, remaining);
                        _audioBufferPosition = remaining;
                    }
                }

                _consecutiveAnalysisErrors = 0;
            }
            catch (Exception ex)
            {
                _audioBufferPosition = 0;
                int errCount = Interlocked.Increment(ref _consecutiveAnalysisErrors);
                LogRateLimitedError($"Error in OnDataAvailable (#{errCount}): {ex.Message}");
                if (errCount >= 10)
                {
                    RequestRecovery("Consecutive audio buffer errors", 500);
                }
            }
        }
    }

    private void PerformAnalysis(int sampleRate)
    {
        try
        {
            _analysisCounter++;

            float energySum = 0f;
            for (int i = 0; i < _bufferSize; i++)
            {
                float s = _audioBuffer[i];
                energySum += s * s;
                _fftBuffer[i].X = s * _hammingWindow[i];
                _fftBuffer[i].Y = 0f;
            }
            float energy = (float)Math.Sqrt(energySum / _bufferSize);
            if (!float.IsFinite(energy)) energy = 0f;

            // Perform FFT first so both spectral-flux BPM estimation and band analysis share the transformed buffer
            int fftLength = (int)Math.Log(_bufferSize, 2.0);
            FastFourierTransform.FFT(true, fftLength, _fftBuffer);

            int safeRate = sampleRate > 0 ? sampleRate : 48000;

            // Spectral-flux autocorrelation BPM detection
            _beatDetector.ProcessFrame(_fftBuffer, safeRate, _bufferSize, _hopSize, energy);

            // Update BPM every 0.5 seconds
            var now = DateTime.UtcNow;
            if ((now - _lastBPMUpdate).TotalSeconds >= 0.5)
            {
                _lastBPM = _beatDetector.GetCurrentBPM();
                _lastBPMUpdate = now;
            }

            float binWidth = (float)safeRate / _bufferSize;

            int minBassBin = Math.Max(1, (int)(20 / binWidth));
            int maxBassBin = Math.Min(_bufferSize / 2 - 1, (int)(BassMaxFreq / binWidth));

            int minTrebleBin = Math.Max(1, (int)(TrebleMinFreq / binWidth));
            int maxTrebleBin = Math.Min(_bufferSize / 2 - 1, (int)(20000 / binWidth));

            float bassSqSum = 0f;
            float trebleSum = 0f;
            int bassCount = 0;
            int trebleCount = 0;
            float maxBassFreq = 0f;
            float maxTrebleFreq = 0f;
            float maxBassMag = 0f;
            float maxTrebleMag = 0f;

            // NAudio.Dsp.FastFourierTransform.FFT(true, ...) already normalizes bins by 1/_bufferSize
            for (int i = minBassBin; i <= maxBassBin; i++)
            {
                float magSq = _fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y;
                float mag = (float)Math.Sqrt(magSq);
                if (!float.IsFinite(mag)) continue;
                bassSqSum += mag * mag;
                bassCount++;
                if (mag > maxBassMag)
                {
                    maxBassMag = mag;
                    maxBassFreq = i * binWidth;
                }
            }

            for (int i = minTrebleBin; i <= maxTrebleBin; i++)
            {
                float magSq = _fftBuffer[i].X * _fftBuffer[i].X + _fftBuffer[i].Y * _fftBuffer[i].Y;
                float mag = (float)Math.Sqrt(magSq);
                if (!float.IsFinite(mag)) continue;
                trebleSum += mag;
                trebleCount++;
                if (mag > maxTrebleMag)
                {
                    maxTrebleMag = mag;
                    maxTrebleFreq = i * binWidth;
                }
            }

            float bassAverage = bassCount > 0 ? (float)Math.Sqrt(bassSqSum / bassCount) : 0f;
            float trebleAverage = trebleCount > 0 ? trebleSum / trebleCount : 0f;

            if (bassAverage > _maxBassLevel) _maxBassLevel = bassAverage;
            if (trebleAverage > _maxTrebleLevel) _maxTrebleLevel = trebleAverage;

            // Skip serialization and broadcasting when no WebSocket clients are attached
            if (!Globals.webSocket.HasConnectedClients)
            {
                return;
            }

            _payload.version = 2;
            _payload.utime = DateTime.UtcNow.Ticks;
            _payload.analysis = _analysisCounter;
            _payload.energy = energy;

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

            byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(_payloadArray, _jsonOptions);
            _ = Globals.webSocket.BroadcastBytesAsync(utf8Bytes);

            if (ConsoleManager.HasConsole())
            {
                string jsonString = System.Text.Encoding.UTF8.GetString(utf8Bytes);
                ConsoleManager.Log(jsonString);
            }
        }
        catch (Exception ex)
        {
            int errCount = Interlocked.Increment(ref _consecutiveAnalysisErrors);
            LogRateLimitedError($"Error in PerformAnalysis (#{errCount}): {ex.Message}");
            if (errCount >= 10)
            {
                RequestRecovery("Consecutive FFT analysis errors", 500);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!_isRunning || !ReferenceEquals(sender, _capture))
        {
            return;
        }

        if (e.Exception != null)
        {
            if (e.Exception is COMException comEx)
            {
                LogRateLimitedError($"Recording stopped (COM): {DescribeCOMException(comEx)}");
            }
            else
            {
                LogRateLimitedError($"Recording stopped due to error: {e.Exception.Message}");
            }
        }
        else
        {
            ConsoleManager.LogEvent("Recording stopped unexpectedly.");
        }

        RequestRecovery("RecordingStopped event", 800);
    }
}
