using System;
using System.Globalization;
using OmenCore.Services;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Reads AMD Ryzen CPU package temperature directly from the SMN Tctl register (0x59800).
    /// Same method HP uses in HP.Omen.Core.Common AMD17CPU.cs.
    /// Also falls back to PM Table if SMN access is locked.
    /// </summary>
    public sealed class RyzenTemperatureProvider : IDisposable
    {
        private const uint SMN_TCTL = 0x59800;
        private const uint SMN_TDIE = 0x59954; // Actual die temperature (CCD1)
        private const uint TCTL_RANGE_SEL_BIT = 0x80000;
        private const int TCTL_VALUE_SHIFT = 21;
        private const uint TCTL_VALUE_MASK = 0x7FF;
        private const double TCTL_VALUE_SCALE = 8.0;
        private const double TCTL_RANGE_OFFSET = 49.0;

        private readonly RyzenSmu _smu;
        private readonly PawnIO.RyzenSmuService? _pmSmu;
        private readonly LoggingService? _logging;
        private readonly bool _isAmd;

        private int _consecutiveFailures;
        private DateTime _disabledUntil = DateTime.MinValue;
        private const int MaxConsecutiveFailures = 5;
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

        private DateTime _lastSuccessLog = DateTime.MinValue;
        private static readonly TimeSpan SuccessLogInterval = TimeSpan.FromSeconds(10);

        public RyzenTemperatureProvider(RyzenSmu? smu, LoggingService? logging = null)
        {
            _smu = smu;
            _logging = logging;
            try { _isAmd = RyzenControl.IsAmd(); }
            catch { _isAmd = false; }
            
            if (_isAmd)
            {
                try 
                {
                    _pmSmu = new PawnIO.RyzenSmuService();
                    var asm = System.Reflection.Assembly.GetEntryAssembly() ?? System.Reflection.Assembly.GetExecutingAssembly();
                    if (!_pmSmu.Initialize(asm))
                    {
                        _pmSmu.Dispose();
                        _pmSmu = null;
                    }
                }
                catch { _pmSmu = null; }
            }
        }

        public bool IsAvailable => _isAmd && ((_smu != null && _smu.IsAvailable) || (_pmSmu != null && _pmSmu.IsInitialized)) && DateTime.UtcNow >= _disabledUntil;

        public double? ReadPackageTemperatureC()
        {
            if (!_isAmd) return null;
            if (DateTime.UtcNow < _disabledUntil) return null;

            try
            {
                double tempC = -1;
                bool readSuccess = false;
                uint usedRegister = 0;

                // 1. Try Tdie / CCD1 (0x59954) first - more accurate/responsive on Zen 2+
                if (_smu != null && _smu.IsAvailable && _smu.ReadSmnRegister(SMN_TDIE, out uint rawDie))
                {
                    double tDie = ((rawDie >> TCTL_VALUE_SHIFT) & TCTL_VALUE_MASK) / TCTL_VALUE_SCALE;
                    if ((rawDie & TCTL_RANGE_SEL_BIT) != 0) tDie -= TCTL_RANGE_OFFSET;

                    if (tDie > 0 && tDie <= 125)
                    {
                        tempC = tDie;
                        readSuccess = true;
                        usedRegister = SMN_TDIE;
                    }
                }

                // 2. Fall back to Tctl (0x59800) if Tdie is 0 or invalid
                if (!readSuccess && _smu != null && _smu.IsAvailable && _smu.ReadSmnRegister(SMN_TCTL, out uint rawTctl))
                {
                    double tCtl = ((rawTctl >> TCTL_VALUE_SHIFT) & TCTL_VALUE_MASK) / TCTL_VALUE_SCALE;
                    if ((rawTctl & TCTL_RANGE_SEL_BIT) != 0) tCtl -= TCTL_RANGE_OFFSET;

                    if (tCtl > 0 && tCtl <= 125)
                    {
                        tempC = tCtl;
                        readSuccess = true;
                        usedRegister = SMN_TCTL;
                    }
                }


                if (!readSuccess)
                {
                    RegisterFailure("SMN/PMTable read returned invalid or out of range temperatures");
                    return null;
                }

                RegisterSuccess(tempC, usedRegister);
                return tempC;
            }
            catch (Exception ex)
            {
                RegisterFailure($"Exception: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private void RegisterSuccess(double tempC, uint smnRegister)
        {
            _consecutiveFailures = 0;
            _disabledUntil = DateTime.MinValue;
            if (DateTime.UtcNow - _lastSuccessLog >= SuccessLogInterval)
            {
                _lastSuccessLog = DateTime.UtcNow;
                string source = smnRegister == 0xFFFFFFFF ? "PM Table" : $"SMN 0x{smnRegister:X5}";
                _logging?.Info($"[RyzenTemp] Temperature = {tempC.ToString("F1", CultureInfo.InvariantCulture)}°C ({source})");
            }
        }

        private void RegisterFailure(string reason)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                _disabledUntil = DateTime.UtcNow.Add(FailureBackoff);
                _logging?.Warn($"[RyzenTemp] Temp read failed {_consecutiveFailures} times ({reason}). Disabling for {FailureBackoff.TotalSeconds:F0}s.");
                _consecutiveFailures = 0;
            }
            else
            {
                _logging?.Debug($"[RyzenTemp] Temp read failed (attempt {_consecutiveFailures}/{MaxConsecutiveFailures}): {reason}");
            }
        }

        public void Dispose()
        {
            try { _smu?.Dispose(); } catch { }
            try { _pmSmu?.Dispose(); } catch { }
        }
    }
}
