using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace reactivo.Classes
{
    internal class BPMDetect
    {
        private readonly Queue<float> _energyHistory;
        private readonly Queue<DateTime> _beatTimes;
        private readonly int _historySize = 43; // ~1 second at ~43 FPS analysis
        private readonly float _beatThreshold = 1.3f; // Energy must be 30% above average
        private readonly int _minBeatInterval = 300; // Minimum 300ms between beats (200 BPM max)
        private DateTime _lastBeatTime = DateTime.MinValue;

        public BPMDetect()
        {
            _energyHistory = new Queue<float>();
            _beatTimes = new Queue<DateTime>();
        }

        public bool DetectBeat(float currentEnergy)
        {
            _energyHistory.Enqueue(currentEnergy);
            if (_energyHistory.Count > _historySize)
                _energyHistory.Dequeue();

            if (_energyHistory.Count < _historySize)
                return false;

            float averageEnergy = _energyHistory.Average();
            float variance = _energyHistory.Sum(e => (e - averageEnergy) * (e - averageEnergy)) / _energyHistory.Count;

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
            if (_beatTimes.Count < 2)
                return 0;

            var beats = _beatTimes.ToArray();
            var intervals = new List<double>();

            for (int i = 1; i < beats.Length; i++)
            {
                intervals.Add((beats[i] - beats[i - 1]).TotalSeconds);
            }

            if (intervals.Count == 0)
                return 0;

            // Use median interval to reduce noise
            intervals.Sort();
            double medianInterval = intervals[intervals.Count / 2];

            return (float)(60.0 / medianInterval);
        }
    }
}
