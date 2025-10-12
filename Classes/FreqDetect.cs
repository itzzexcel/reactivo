using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using reactivo.Classes;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace reactivo.Classes;

public class FrequencyDetector
{
    private WasapiLoopbackCapture? _capture;
    private readonly int _bufferSize = 2048;
    private Complex[] _fftBuffer;
    private float[] _audioBuffer;
    private int _audioBufferPosition = 0;

    // Thresholds
    private const float BassThreshold = 0.001f;
    private const float TrebleThreshold = 0.001f;

    // Frequency ranges (Hz)
    private const int BassMaxFreq = 200;
    private const int TrebleMinFreq = 4000;

    public event Action<bool, bool, float, float, bool, float>? FrequencyDetected; // (hasBass, hasTreble, bassLevel, trebleLevel, beatDetected, currentBPM)

    private int _analysisCounter = 0;
    private float _maxBassLevel = 0;
    private float _maxTrebleLevel = 0;
    private BPMDetect _beatDetector;
    private float _lastBPM = 0;
    private DateTime _lastBPMUpdate = DateTime.MinValue;


    public FrequencyDetector()
    {
        _fftBuffer = new Complex[_bufferSize];
        _audioBuffer = new float[_bufferSize];
        _beatDetector = new BPMDetect();
    }

    public void StartMonitoring()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);

        Console.WriteLine($"Monitoring audio from: {device.FriendlyName}");

        _capture = new WasapiLoopbackCapture(device);
        _capture.DataAvailable += OnDataAvailable!;
        _capture.RecordingStopped += OnRecordingStopped!;

        Console.WriteLine($"Sample Rate: {_capture.WaveFormat.SampleRate} Hz");
        Console.WriteLine($"Channels: {_capture.WaveFormat.Channels}");
        Console.WriteLine($"Bits per Sample: {_capture.WaveFormat.BitsPerSample}");

        _capture.StartRecording();

        Console.WriteLine("Audio monitoring started. Press 'q' to quit.");
        Console.WriteLine("Monitoring: Bass (20-250 Hz), Treble (4000-20000 Hz), and Tempo (BPM)");
        Console.WriteLine("Debug info will show every 50 analyses...\n");
    }

    public void StopMonitoring()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }

    private void OnDataAvailable(object sender, WaveInEventArgs e)
    {
        try
        {
            int bytesPerSample = _capture!.WaveFormat.BitsPerSample / 8;
            int channels = _capture!.WaveFormat.Channels;
            int samplesAvailable = e.BytesRecorded / bytesPerSample;

            for (int i = 0; i < samplesAvailable; i += channels)
            {
                float sample;

                if (_capture!.WaveFormat.BitsPerSample == 32)
                {
                    sample = BitConverter.ToSingle(e.Buffer, i * bytesPerSample);
                }
                else if (_capture!.WaveFormat.BitsPerSample == 16)
                {
                    sample = BitConverter.ToInt16(e.Buffer, i * bytesPerSample) / 32768.0f;
                }
                else
                {
                    continue;
                }

                _audioBuffer[_audioBufferPosition] = sample;
                _audioBufferPosition++;

                if (_audioBufferPosition >= _bufferSize)
                {
                    PerformAnalysis();
                    _audioBufferPosition = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in OnDataAvailable: {ex.Message}");
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
            if (_analysisCounter % 10 == 0)
            {
                Console.WriteLine($"Debug #{_analysisCounter}: Energy={energy:F6}, Bass={bassAverage:F6} (max={_maxBassLevel:F6}), " +
                                    $"Treble={trebleAverage:F6} (max={_maxTrebleLevel:F6}), BPM={_lastBPM:F1}");
                if (maxBassMag > 0)
                    Console.WriteLine($"  Strongest Bass: {maxBassFreq:F0}Hz @ {maxBassMag:F6}");
                if (maxTrebleMag > 0)
                    Console.WriteLine($"  Strongest Treble: {maxTrebleFreq:F0}Hz @ {maxTrebleMag:F6}");
            }

            // Trigger event
            FrequencyDetected?.Invoke(hasBass, hasTreble, bassAverage, trebleAverage, beatDetected, _lastBPM);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in PerformAnalysis: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Console.WriteLine($"Recording stopped due to error: {e.Exception.Message}");
        }
        else
        {
            Console.WriteLine("Recording stopped.");
        }
    }

}