using NAudio.Dsp;
using System;

namespace reactivo.Classes
{
    /// <summary>
    /// Multi-band spectral-flux autocorrelation + harmonic comb-filter BPM estimator.
    /// Operates in real time across arbitrary sample/frame rates with zero per-frame heap allocations.
    /// </summary>
    internal class BPMDetect
    {
        private const int MaxTrackedBins = 512;
        private const int RingSize = 1024;
        private const int MaxLocalFluxWindow = 32;
        private const int MaxLagCap = 420;
        private const int RecentEstimateSlots = 7;

        private readonly float[] _prevLogMag = new float[MaxTrackedBins];
        private readonly float[] _onsetRing = new float[RingSize];
        private readonly float[] _localFluxRing = new float[MaxLocalFluxWindow];
        private readonly float[] _autocorr = new float[MaxLagCap + 1];
        private readonly float[] _recentEstimates = new float[RecentEstimateSlots];

        private bool _hasPrevSpectrum;
        private float _lastFrameRate;
        private float _silenceSeconds;

        private int _localFluxHead;
        private int _localFluxCount;
        private float _localFluxSum;

        private int _onsetHead;
        private int _onsetCount;
        private int _framesSinceEval;

        private int _recentHead;
        private int _recentCount;

        private float _lockedBpm;
        private float _pendingBpm;
        private int _pendingChangeCount;

        public void ProcessFrame(Complex[] fftBuffer, int sampleRate, int fftSize, int hopSize, float broadbandEnergy)
        {
            if (fftBuffer == null || sampleRate <= 0 || fftSize < 8 || hopSize <= 0 || fftBuffer.Length < fftSize)
                return;

            if (!float.IsFinite(broadbandEnergy) || broadbandEnergy < 0f)
                broadbandEnergy = 0f;

            float frameRate = sampleRate / (float)hopSize;
            if (!float.IsFinite(frameRate) || frameRate <= 1f)
                return;

            if (_lastFrameRate > 0f && MathF.Abs(frameRate - _lastFrameRate) > 1.0f)
            {
                Reset();
            }
            _lastFrameRate = frameRate;

            if (broadbandEnergy < 0.004f)
            {
                _silenceSeconds += 1f / frameRate;
                if (_silenceSeconds >= 1.8f)
                {
                    ClearTempoState();
                }
                return;
            }
            _silenceSeconds = 0f;

            float binWidth = sampleRate / (float)fftSize;
            int maxPossibleBin = Math.Min(MaxTrackedBins - 1, fftSize / 2 - 1);
            int minBin = Math.Clamp((int)MathF.Floor(30f / binWidth), 1, maxPossibleBin);
            int maxBin = Math.Clamp((int)MathF.Ceiling(8000f / binWidth), minBin, maxPossibleBin);

            int kickEndBin = (int)(160f / binWidth);
            int midEndBin = (int)(2500f / binWidth);

            float weightedFlux = 0f;
            float weightSum = 0f;

            for (int bin = minBin; bin <= maxBin; bin++)
            {
                float re = fftBuffer[bin].X;
                float im = fftBuffer[bin].Y;
                float magSq = re * re + im * im;
                float mag = float.IsFinite(magSq) && magSq > 0f ? MathF.Sqrt(magSq) : 0f;
                float logMag = MathF.Log(1f + 40f * mag);

                if (_hasPrevSpectrum)
                {
                    float diff = logMag - _prevLogMag[bin];
                    float weight = bin <= kickEndBin ? 2.2f : (bin <= midEndBin ? 1.0f : 0.5f);
                    if (diff > 0f)
                    {
                        weightedFlux += diff * weight;
                    }
                    weightSum += weight;
                }

                _prevLogMag[bin] = logMag;
            }

            if (!_hasPrevSpectrum)
            {
                _hasPrevSpectrum = true;
                return;
            }

            float rawFlux = weightSum > 0f ? weightedFlux / weightSum : 0f;

            // Local adaptive mean subtraction (~0.35s sliding window)
            int localWindow = Math.Clamp((int)(frameRate * 0.35f), 8, MaxLocalFluxWindow);
            if (_localFluxCount >= localWindow)
            {
                _localFluxSum = MathF.Max(0f, _localFluxSum - _localFluxRing[_localFluxHead]);
            }
            else
            {
                _localFluxCount++;
            }
            _localFluxRing[_localFluxHead] = rawFlux;
            _localFluxSum += rawFlux;
            _localFluxHead = (_localFluxHead + 1) % localWindow;

            float localMean = _localFluxSum / Math.Max(1, _localFluxCount);
            float onset = MathF.Max(0f, rawFlux - localMean);

            _onsetRing[_onsetHead] = onset;
            _onsetHead = (_onsetHead + 1) % RingSize;
            if (_onsetCount < RingSize)
            {
                _onsetCount++;
            }

            _framesSinceEval++;
            int evalStep = Math.Max(8, (int)(frameRate * 0.5f));
            int minFramesNeeded = Math.Max(64, (int)(frameRate * 2.5f));

            if (_framesSinceEval >= evalStep && _onsetCount >= minFramesNeeded)
            {
                _framesSinceEval = 0;
                EstimateTempo(frameRate);
            }
        }

        private void EstimateTempo(float frameRate)
        {
            int windowLen = Math.Min(_onsetCount, Math.Clamp((int)(frameRate * 8.0f), 128, RingSize));
            int minCandidateLag = Math.Max(4, (int)MathF.Floor(frameRate * 60f / 190f));
            int maxCandidateLag = Math.Min(MaxLagCap / 2, (int)MathF.Ceiling(frameRate * 60f / 58f));
            int maxEvalLag = Math.Min(MaxLagCap, Math.Min(windowLen - 8, maxCandidateLag * 2 + 2));

            if (maxCandidateLag <= minCandidateLag + 1 || maxEvalLag <= minCandidateLag)
                return;

            int startIdx = (_onsetHead - windowLen + RingSize) % RingSize;

            float energySum = 0f;
            for (int i = 0; i < windowLen; i++)
            {
                float v = _onsetRing[(startIdx + i) % RingSize];
                energySum += v * v;
            }
            float rms = MathF.Sqrt(energySum / windowLen);
            if (rms < 1e-5f)
                return;

            float rmsSq = rms * rms;

            Array.Clear(_autocorr, 0, maxEvalLag + 1);
            for (int lag = 2; lag <= maxEvalLag; lag++)
            {
                int n = windowLen - lag;
                if (n <= 4) break;

                float sum = 0f;
                float wSum = 0f;
                float invN = 1f / n;
                for (int i = 0; i < n; i++)
                {
                    float a = _onsetRing[(startIdx + i) % RingSize];
                    float b = _onsetRing[(startIdx + i + lag) % RingSize];
                    float weight = 0.65f + 0.35f * (i * invN);
                    sum += a * b * weight;
                    wSum += weight;
                }

                float corr = (wSum > 0f) ? (sum / (wSum * rmsSq)) : 0f;
                _autocorr[lag] = MathF.Max(0f, corr);
            }

            Span<float> scores = stackalloc float[maxCandidateLag + 2];
            scores.Clear();

            int bestLag = -1;
            float bestScore = 0f;
            float scoreSum = 0f;
            int candidateCount = 0;

            for (int lag = minCandidateLag; lag <= maxCandidateLag; lag++)
            {
                float comb = _autocorr[lag];
                int twoLag = lag * 2;
                int threeLag = lag * 3;
                int halfLag = lag / 2;

                if (twoLag <= maxEvalLag) comb += 0.45f * _autocorr[twoLag];
                if (threeLag <= maxEvalLag) comb += 0.15f * _autocorr[threeLag];
                if (halfLag >= 2) comb += 0.22f * _autocorr[halfLag];

                float candBpm = 60f * frameRate / lag;
                float octDist = MathF.Log2(candBpm / 120f);
                float normOct = octDist / 0.58f;
                float prior = MathF.Exp(-0.5f * normOct * normOct);
                float score = comb * (0.45f + 0.55f * prior);

                scores[lag] = score;
                scoreSum += score;
                candidateCount++;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestLag = lag;
                }
            }

            if (bestLag < minCandidateLag || bestScore <= 0f || candidateCount == 0)
                return;

            float meanScore = scoreSum / candidateCount;
            if (bestScore / MathF.Max(meanScore, 1e-6f) < 1.35f)
                return;

            float refinedLag = bestLag;
            if (bestLag > minCandidateLag && bestLag < maxCandidateLag)
            {
                float sPrev = scores[bestLag - 1];
                float sCurr = scores[bestLag];
                float sNext = scores[bestLag + 1];
                float denom = sPrev - 2f * sCurr + sNext;
                if (MathF.Abs(denom) > 1e-7f)
                {
                    float delta = Math.Clamp(0.5f * (sPrev - sNext) / denom, -0.5f, 0.5f);
                    refinedLag = bestLag + delta;
                }
            }

            float rawBpm = Math.Clamp(60f * frameRate / MathF.Max(1f, refinedLag), 58f, 195f);
            ApplyTemporalStabilization(rawBpm);
        }

        private void ApplyTemporalStabilization(float rawBpm)
        {
            _recentEstimates[_recentHead] = rawBpm;
            _recentHead = (_recentHead + 1) % RecentEstimateSlots;
            if (_recentCount < RecentEstimateSlots)
            {
                _recentCount++;
            }

            if (_lockedBpm <= 0f)
            {
                if (_recentCount >= 2)
                {
                    Span<float> sorted = stackalloc float[_recentCount];
                    for (int i = 0; i < _recentCount; i++)
                    {
                        sorted[i] = _recentEstimates[i];
                    }
                    sorted.Sort();
                    _lockedBpm = sorted[_recentCount / 2];
                }
                return;
            }

            float relDiff = MathF.Abs(rawBpm - _lockedBpm) / _lockedBpm;
            if (relDiff <= 0.06f)
            {
                _lockedBpm = _lockedBpm * 0.72f + rawBpm * 0.28f;
                _pendingChangeCount = 0;
                return;
            }

            bool isOctaveRelated =
                (MathF.Abs(rawBpm - _lockedBpm * 2f) / (_lockedBpm * 2f) <= 0.06f) ||
                (MathF.Abs(rawBpm - _lockedBpm * 0.5f) / (_lockedBpm * 0.5f) <= 0.06f);

            int requiredConfirmations = isOctaveRelated ? 4 : 3;

            if (_pendingChangeCount > 0 && MathF.Abs(rawBpm - _pendingBpm) / MathF.Max(1f, _pendingBpm) <= 0.06f)
            {
                _pendingBpm = (_pendingBpm + rawBpm) * 0.5f;
                _pendingChangeCount++;
            }
            else
            {
                _pendingBpm = rawBpm;
                _pendingChangeCount = 1;
            }

            if (_pendingChangeCount >= requiredConfirmations)
            {
                _lockedBpm = _pendingBpm;
                _pendingChangeCount = 0;
            }
        }

        public float GetCurrentBPM()
        {
            return float.IsFinite(_lockedBpm) && _lockedBpm > 0f ? MathF.Round(_lockedBpm, 1) : 0f;
        }

        private void ClearTempoState()
        {
            Array.Clear(_prevLogMag, 0, _prevLogMag.Length);
            Array.Clear(_onsetRing, 0, _onsetRing.Length);
            Array.Clear(_localFluxRing, 0, _localFluxRing.Length);
            Array.Clear(_recentEstimates, 0, _recentEstimates.Length);

            _hasPrevSpectrum = false;
            _localFluxHead = 0;
            _localFluxCount = 0;
            _localFluxSum = 0f;
            _onsetHead = 0;
            _onsetCount = 0;
            _framesSinceEval = 0;
            _recentHead = 0;
            _recentCount = 0;
            _lockedBpm = 0f;
            _pendingBpm = 0f;
            _pendingChangeCount = 0;
        }

        public void Reset()
        {
            ClearTempoState();
            _silenceSeconds = 0f;
            _lastFrameRate = 0f;
        }
    }
}
