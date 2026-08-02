using System;
using System.Collections.Generic;
using System.Linq;

namespace OmenCore.Hardware.Adaptive
{
    /// <summary>
    /// Classifies the current GPU workload from a rolling window of
    /// (gpu_util%, mem_util%) samples. Returns both the raw class and
    /// whether it has been stable long enough to trust for learning.
    /// </summary>
    /// <remarks>
    /// This class is NOT thread-safe. It is intended to be called from
    /// a single thread (the power control loop).
    /// </remarks>
    public sealed class WorkloadClassifier
    {
        // Window: 6 samples at 500ms = 3s rolling window
        private const int WindowSize = 6;

        // Variance threshold above which we declare the workload TRANSIENT
        private const double GpuStdTransientThreshold = 15.0;

        // A class must hold for this long before we mark it stable
        private const long StabilityMs = 3000;

        // Classification thresholds (percent)
        private const double IdleGpu      = 5;
        private const double IdleMem      = 5;
        private const double LightGpu     = 30;
        private const double LightMem     = 30;
        private const double ComputeGpu   = 80;
        private const double ComputeMem   = 30;
        private const double MemBoundGpu  = 50;
        private const double MemBoundMem  = 70;
        private const double GamingGpu    = 50;

        private readonly Queue<(uint gpu, uint mem, long tick)> _window = new();
        private WorkloadClass _lastRawClass = WorkloadClass.Transient;
        private long _classChangeTick;

        /// <summary>
        /// Push a new util sample and return (currentClass, isStable).
        /// </summary>
        public (WorkloadClass current, bool isStable) Classify(uint gpuUtil, uint memUtil)
        {
            long now = Environment.TickCount64;
            _window.Enqueue((gpuUtil, memUtil, now));
            while (_window.Count > WindowSize) _window.Dequeue();

            // Not enough data yet — always Transient
            if (_window.Count < WindowSize)
                return (WorkloadClass.Transient, false);

            var gpuValues = _window.Select(s => (double)s.gpu).ToArray();
            var memValues = _window.Select(s => (double)s.mem).ToArray();

            double gpuAvg = gpuValues.Average();
            double memAvg = memValues.Average();
            double gpuStd = StdDev(gpuValues);

            WorkloadClass raw;
            if (gpuStd > GpuStdTransientThreshold)
                raw = WorkloadClass.Transient;
            else if (gpuAvg < IdleGpu && memAvg < IdleMem)
                raw = WorkloadClass.Idle;
            else if (gpuAvg < LightGpu && memAvg < LightMem)
                raw = WorkloadClass.Light;
            else if (gpuAvg > ComputeGpu && memAvg < ComputeMem)
                raw = WorkloadClass.Compute;
            else if (memAvg > MemBoundMem && gpuAvg < MemBoundGpu)
                raw = WorkloadClass.MemBound;
            else if (gpuAvg > GamingGpu)
                raw = WorkloadClass.Gaming;
            else
                raw = WorkloadClass.Light;

            bool isStable;
            if (raw == _lastRawClass)
            {
                isStable = (now - _classChangeTick) >= StabilityMs;
            }
            else
            {
                _lastRawClass   = raw;
                _classChangeTick = now;
                isStable        = false;
            }

            return (raw, isStable);
        }

        /// <summary>Last raw classification (may not yet be stable).</summary>
        public WorkloadClass LastRawClass => _lastRawClass;

        /// <summary>Reset the window (e.g. after controller restart).</summary>
        public void Reset()
        {
            _window.Clear();
            _lastRawClass   = WorkloadClass.Transient;
            _classChangeTick = 0;
        }

        private static double StdDev(double[] values)
        {
            if (values.Length < 2) return 0;
            double mean = values.Average();
            double sum  = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sum / values.Length);
        }
    }
}
