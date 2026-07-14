using System;
using System.Globalization;
using OmenCore.Services;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Reads AMD Ryzen CPU package temperature directly from the SMN Tctl register (0x59800).
    /// Same method HP uses in HP.Omen.Core.Common AMD17CPU.cs.
    /// </summary>
    public sealed class RyzenTemperatureProvider : IDisposable
    {
        private const uint SMN_TCTL = 0x59800;
        private const uint TCTL_RANGE_SEL_BIT = 0x80000;
        private const int TCTL_VALUE_SHIFT = 21;
        private const uint TCTL_VALUE_MASK = 0x7FF;
        private const double TCTL_VALUE_SCALE = 8.0;
        private const double TCTL_RANGE_OFFSET = 49.0;

        private readonly RyzenSmu _smu;
        private readonly LoggingService? _logging;
        private readonly bool _isAmd;

        private int _consecutiveFailures;
        private DateTime _disabledUntil = DateTime.MinValue;
        private const int MaxConsecutiveFailures = 5;
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

        private DateTime _lastSuccessLog = DateTime.MinValue;
        private static readonly TimeSpan SuccessLogInterval = TimeSpan.FromSeconds(10);

        public RyzenTemperatureProvider(RyzenSmu smu, LoggingService? logging = null)
        {
            _smu = smu ?? throw new ArgumentNullException(nameof(smu));
            _logging = logging;
            try { _isAmd = RyzenControl.IsAmd(); }
            catch { _isAmd = false; }
        }

        public bool IsAvailable => _isAmd && _smu.IsAvailable && DateTime.UtcNow >= _disabledUntil;

        public double? ReadPackageTemperatureC()
        {
            if (!_isAmd) return null;
            if (DateTime.UtcNow < _disabledUntil) return null;
            if (!_smu.IsAvailable)
            {
                _logging?.Debug("[RyzenTemp] SMU not available — cannot read Tctl");
                return null;
            }

            try
            {
                if (!_smu.ReadSmnRegister(SMN_TCTL, out uint raw))
                {
                    RegisterFailure("SMN read returned false");
                    return null;
                }

                double tempC = ((raw >> TCTL_VALUE_SHIFT) & TCTL_VALUE_MASK) / TCTL_VALUE_SCALE;

                if ((raw & TCTL_RANGE_SEL_BIT) != 0)
                    tempC -= TCTL_RANGE_OFFSET;

                if (tempC < 0 || tempC > 125)
                {
                    _logging?.Warn($"[RyzenTemp] SMN Tctl reading out of range: {tempC}°C (raw=0x{raw:X8}) — ignoring");
                    return null;
                }

                RegisterSuccess(tempC);
                return tempC;
            }
            catch (Exception ex)
            {
                RegisterFailure($"Exception: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private void RegisterSuccess(double tempC)
        {
            _consecutiveFailures = 0;
            _disabledUntil = DateTime.MinValue;
            if (DateTime.UtcNow - _lastSuccessLog >= SuccessLogInterval)
            {
                _lastSuccessLog = DateTime.UtcNow;
                _logging?.Info($"[RyzenTemp] Tctl = {tempC.ToString("F1", CultureInfo.InvariantCulture)}°C (SMN 0x{SMN_TCTL:X5})");
            }
        }

        private void RegisterFailure(string reason)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                _disabledUntil = DateTime.UtcNow.Add(FailureBackoff);
                _logging?.Warn($"[RyzenTemp] SMN Tctl read failed {_consecutiveFailures} times ({reason}). Disabling for {FailureBackoff.TotalSeconds:F0}s.");
                _consecutiveFailures = 0;
            }
            else
            {
                _logging?.Debug($"[RyzenTemp] SMN Tctl read failed (attempt {_consecutiveFailures}/{MaxConsecutiveFailures}): {reason}");
            }
        }

        public void Dispose()
        {
            try { _smu?.Dispose(); } catch { }
        }
    }
}
