using System;
using System.Linq;

namespace OmenCore.Models
{
    /// <summary>
    /// Central safety limits for CPU/GPU tuning requests.
    /// </summary>
    public static class TuningGuardrails
    {
        public const double IntelCpuUndervoltMinMv = -150;
        public const double AmdCurveOptimizerEquivalentMinMv = -120;
        public const double CpuUndervoltMaxMv = 0;
        public const int GpuVoltageOffsetMinMv = -200;
        public const int GpuVoltageOffsetMaxMv = 100;

        public static double ClampCpuUndervoltMv(double value, bool amdCurveOptimizer)
        {
            var min = amdCurveOptimizer ? AmdCurveOptimizerEquivalentMinMv : IntelCpuUndervoltMinMv;
            return Math.Clamp(value, min, CpuUndervoltMaxMv);
        }

        public static int ClampCpuUndervoltMv(int value, bool amdCurveOptimizer)
        {
            var min = amdCurveOptimizer ? (int)AmdCurveOptimizerEquivalentMinMv : (int)IntelCpuUndervoltMinMv;
            return Math.Clamp(value, min, (int)CpuUndervoltMaxMv);
        }

        public static UndervoltOffset ClampCpuUndervoltOffset(UndervoltOffset offset, bool amdCurveOptimizer)
        {
            return new UndervoltOffset
            {
                CoreMv = ClampCpuUndervoltMv(offset.CoreMv, amdCurveOptimizer),
                CacheMv = ClampCpuUndervoltMv(offset.CacheMv, amdCurveOptimizer),
                PerCoreOffsetsMv = offset.PerCoreOffsetsMv?
                    .Select(x => x.HasValue ? ClampCpuUndervoltMv(x.Value, amdCurveOptimizer) : (int?)null)
                    .ToArray()
            };
        }

        public static int ClampGpuVoltageOffsetMv(int value)
        {
            return Math.Clamp(value, GpuVoltageOffsetMinMv, GpuVoltageOffsetMaxMv);
        }
    }
}
