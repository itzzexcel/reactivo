namespace reactivo.Classes
{
    internal class BPMDetect
    {
        private readonly float[] _energyBuffer;
        private int _historyIndex = 0;
        private int _historyCount = 0;
        private float _runningEnergySum = 0f;
        private float _runningEnergySqSum = 0f;

        private readonly Queue<DateTime> _beatTimes;
        private readonly int _historySize = 47; // ~1 second at ~47 FPS analysis (1024 hop size)
        private readonly float _beatThreshold = 1.3f; // Energy must be 30% above average
        private readonly int _minBeatInterval = 300; // Minimum 300ms between beats (200 BPM max)
        private DateTime _lastBeatTime = DateTime.MinValue;

        public BPMDetect()
        {
            _energyBuffer = new float[_historySize];
            _beatTimes = new Queue<DateTime>(36);
        }

        public bool DetectBeat(float currentEnergy)
        {
            if (_historyCount == _historySize)
            {
                float oldVal = _energyBuffer[_historyIndex];
                _runningEnergySum -= oldVal;
                _runningEnergySqSum -= oldVal * oldVal;
            }
            else
            {
                _historyCount++;
            }

            _energyBuffer[_historyIndex] = currentEnergy;
            _runningEnergySum += currentEnergy;
            _runningEnergySqSum += currentEnergy * currentEnergy;
            _historyIndex = (_historyIndex + 1) % _historySize;

            if (_historyCount < _historySize)
                return false;

            float averageEnergy = _runningEnergySum / _historySize;
            float variance = (_runningEnergySqSum / _historySize) - (averageEnergy * averageEnergy);

            var now = DateTime.Now;
            bool isBeat = currentEnergy > (averageEnergy * _beatThreshold) &&
                          variance > 0.01f &&
                          (now - _lastBeatTime).TotalMilliseconds > _minBeatInterval;

            if (isBeat)
            {
                _lastBeatTime = now;
                _beatTimes.Enqueue(now);

                // Last 10 seconds scan
                while (_beatTimes.Count > 0 && (now - _beatTimes.Peek()).TotalSeconds > 10)
                    _beatTimes.Dequeue();
            }

            return isBeat;
        }

        public float GetCurrentBPM()
        {
            int count = _beatTimes.Count;
            if (count < 2)
                return 0f;

            // Zero-allocation stackalloc buffer for interval calculation
            Span<double> intervals = stackalloc double[count - 1];
            DateTime prev = DateTime.MinValue;
            int idx = 0;

            foreach (var time in _beatTimes)
            {
                if (prev != DateTime.MinValue && idx < intervals.Length)
                {
                    intervals[idx++] = (time - prev).TotalSeconds;
                }
                prev = time;
            }

            if (idx == 0)
                return 0f;

            // Use median interval to reduce noise
            intervals.Slice(0, idx).Sort();
            double medianInterval = intervals[idx / 2];

            return medianInterval > 0.001 ? (float)(60.0 / medianInterval) : 0f;
        }
    }
}
