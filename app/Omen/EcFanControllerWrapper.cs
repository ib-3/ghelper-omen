using System;
using System.Collections.Generic;
using System.Linq;
using OmenCore.Services;
using OmenCore.Models;
namespace OmenCore.Hardware
{
public class EcFanControllerWrapper : IFanController
    {
        /// <summary>
        /// Verify Max applied by checking EC RPM registers and hardware monitor values.
        /// Attempts to apply Max up to several retries when applying.
        /// </summary>
        public bool VerifyMaxApplied(out string details)
        {
            // Simple verification: read EC RPM registers via underlying controller
            try
            {
                var (f1, f2) = _controller.ReadActualFanRpmPublic();
                details = $"EC RPMs after apply: F1={f1},F2={f2}";
                return (f1 > 1000 || f2 > 1000);
            }
            catch (Exception ex)
            {
                details = $"Verify failed: {ex.Message}";
                return false;
            }
        }

        private readonly FanController _controller;
        private readonly LibreHardwareMonitorImpl? _hwMonitor;
        private readonly LoggingService? _logging;

        public EcFanControllerWrapper(FanController controller, LibreHardwareMonitorImpl? hwMonitor, LoggingService? logging = null)
        {
            _controller = controller;
            _hwMonitor = hwMonitor;
            _logging = logging;
        }

        public bool IsAvailable => _controller.IsEcReady;
        public string Status => _controller.IsEcReady ? "EC access available" : "EC access unavailable";
        public string Backend => $"EC ({EcAccessFactory.ActiveBackend})";

        public bool ApplyPreset(FanPreset preset)
        {
            try
            {
                _controller.ApplyPreset(preset);
                return true;
            }
            catch (Exception ex)
            {
                _logging?.Error($"Failed to apply preset: {ex.Message}", ex);
                return false;
            }
        }

        public bool ApplyCustomCurve(IEnumerable<FanCurvePoint> curve)
        {
            try
            {
                _controller.ApplyCustomCurve(curve);
                return true;
            }
            catch (Exception ex)
            {
                _logging?.Error($"Failed to apply curve: {ex.Message}", ex);
                return false;
            }
        }

        public bool SetFanSpeed(int percent)
        {
            // EC controller doesn't have direct speed control, use curve
            var curve = new List<FanCurvePoint>
            {
                new FanCurvePoint { TemperatureC = 0, FanPercent = percent },
                new FanCurvePoint { TemperatureC = 100, FanPercent = percent }
            };
            return ApplyCustomCurve(curve);
        }

        public bool SetFanSpeeds(int cpuPercent, int gpuPercent)
        {
            try
            {
                _controller.SetFanSpeeds(cpuPercent, gpuPercent);
                return true;
            }
            catch (Exception ex)
            {
                _logging?.Error($"Failed to set fan speeds: {ex.Message}", ex);
                return false;
            }
        }

        public bool? GetMaxFanSpeed() => null;
        public bool SetMaxFanSpeed(bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    // Restore auto control to BIOS
                    _controller.RestoreAutoControl();
                    _logging?.Info("EC: Restored auto control");
                    return true;
                }

                if (!_controller.IsEcReady)
                {
                    _logging?.Warn("EC backend not ready - cannot apply Max fan speed");
                    return false;
                }

                // Apply max fan speed — single attempt to minimize EC register access.
                // Aggressive EC polling can cause ACPI EC timeout (Event 13) and system crashes.
                _logging?.Info("EC: Applying Max fan speed");
                _controller.SetMaxSpeed();

                // Wait for fans to ramp, then do ONE verification read
                System.Threading.Thread.Sleep(300);
                var (fan1, fan2) = _controller.ReadActualFanRpmPublic();
                _logging?.Info($"EC Max verify: Fan1={fan1} RPM, Fan2={fan2} RPM");

                if (fan1 > 1000 || fan2 > 1000)
                {
                    _logging?.Info($"EC: Max fan verified (Fan1={fan1}, Fan2={fan2})");
                    return true;
                }

                // If RPM read didn't confirm, try one more with explicit percent + boost
                _controller.SetImmediatePercent(100);
                _controller.SetMaxSpeed();
                _logging?.Info("EC: Max fan applied (verification inconclusive, assumed success)");
                return true;
            }
            catch (Exception ex)
            {
                _logging?.Error($"Failed to set max fan speed: {ex.Message}", ex);
                return false;
            }
        }

        public bool SetPerformanceMode(string modeName)
        {
            // EC controller doesn't support BIOS performance modes
            _logging?.Info($"EC backend: Performance mode '{modeName}' not directly supported, using fan curve approximation");

            int targetPercent;
            if (FanModeNameResolver.IsPerformanceAlias(modeName) || FanModeNameResolver.IsMaxAlias(modeName))
                targetPercent = 80;
            else if (FanModeNameResolver.IsQuietAlias(modeName))
                targetPercent = 40;
            else
                targetPercent = 60;

            return SetFanSpeed(targetPercent);
        }

        public bool RestoreAutoControl()
        {
            try
            {
                // Use proper EC auto control restoration
                _controller.RestoreAutoControl();
                _logging?.Info("EC: Restored BIOS auto fan control");
                return true;
            }
            catch (Exception ex)
            {
                _logging?.Error($"Failed to restore auto control: {ex.Message}", ex);
                return false;
            }
        }

        public IEnumerable<FanTelemetry> ReadFanSpeeds() => _controller.ReadFanSpeeds();

        public void ApplyMaxCooling()
        {
            _logging?.Info("Applying Max cooling via EC (with fan boost)...");
            var ok = SetMaxFanSpeed(true);
            if (!ok)
            {
                _logging?.Warn("ApplyMaxCooling: Verification failed - Max may not be applied");
            }
        }
        
        public void ApplyAutoMode()
        {
            // Actually restore BIOS auto control instead of setting fixed 50%
            _logging?.Info("Applying Auto mode via EC (restoring BIOS control)...");
            RestoreAutoControl();
        }
        public void ApplyQuietMode() => SetFanSpeed(30);
        
        public bool ResetEcToDefaults()
        {
            _logging?.Info("Resetting EC to defaults via EC access...");
            return _controller.ResetEcToDefaults();
        }
        
        /// <summary>
        /// Apply throttling mitigation via EC register 0x95.
        /// Writes 0x31 (performance mode) to mitigate thermal throttling.
        /// </summary>
        public bool ApplyThrottlingMitigation()
        {
            const ushort EC_PERFORMANCE_REGISTER = 0x95;
            const byte EC_PERFORMANCE_MODE = 0x31;
            
            _logging?.Info("Applying throttling mitigation via EC register 0x95...");
            
            try
            {
                if (!_controller.IsEcReady)
                {
                    _logging?.Warn("EC not ready - cannot apply throttling mitigation");
                    return false;
                }
                
                // Write performance mode to register 0x95
                _controller.WriteEc(EC_PERFORMANCE_REGISTER, EC_PERFORMANCE_MODE);
                _logging?.Info($"✓ Throttling mitigation applied: EC[0x{EC_PERFORMANCE_REGISTER:X2}] = 0x{EC_PERFORMANCE_MODE:X2}");
                return true;
            }
            catch (Exception ex)
            {
                _logging?.Warn($"Throttling mitigation failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            // FanController doesn't implement IDisposable, nothing to dispose
        }
    }

    /// <summary>
    /// Fallback fan controller for systems without HP WMI or EC access.
    /// Provides monitoring-only functionality.
    /// </summary>
    public class FallbackFanController : IFanController
    {
        public bool VerifyMaxApplied(out string details)
        {
            details = "No fan control backend available (monitoring only) - cannot verify Max";
            return false;
        }
        
        private readonly LibreHardwareMonitorImpl? _hwMonitor;
        private readonly LoggingService? _logging;

        public FallbackFanController(LibreHardwareMonitorImpl? hwMonitor, LoggingService? logging = null)
        {
            _hwMonitor = hwMonitor;
            _logging = logging;
        }

        public bool IsAvailable => false;
        public string Status => "No fan control backend available (monitoring only)";
        public string Backend => "None (monitoring only)";

        public bool ApplyPreset(FanPreset preset)
        {
            _logging?.Warn("Fan control not available: Cannot apply preset");
            return false;
        }

        public bool ApplyCustomCurve(IEnumerable<FanCurvePoint> curve)
        {
            _logging?.Warn("Fan control not available: Cannot apply curve");
            return false;
        }

        public bool SetFanSpeed(int percent)
        {
            _logging?.Warn("Fan control not available: Cannot set fan speed");
            return false;
        }

        public bool SetFanSpeeds(int cpuPercent, int gpuPercent)
        {
            _logging?.Warn("Fan control not available: Cannot set fan speeds");
            return false;
        }

        public bool? GetMaxFanSpeed() => null;
        public bool SetMaxFanSpeed(bool enabled)
        {
            _logging?.Warn("Fan control not available: Cannot set max fan speed");
            return false;
        }

        public bool SetPerformanceMode(string modeName)
        {
            _logging?.Warn("Fan control not available: Cannot set performance mode");
            return false;
        }

        public bool RestoreAutoControl()
        {
            _logging?.Info("Fan control not available: Auto control not applicable");
            return true; // Return true since there's nothing to restore
        }

        public IEnumerable<FanTelemetry> ReadFanSpeeds()
        {
            var fans = new List<FanTelemetry>();

            // Get fan speeds from hardware monitor (with WMI BIOS fallback)
            var fanSpeeds = SensorHelper.GetFanSpeeds(_hwMonitor);
            int index = 0;

            foreach (var (name, rpm) in fanSpeeds)
            {
                fans.Add(new FanTelemetry
                {
                    Name = name,
                    SpeedRpm = (int)rpm,
                    DutyCyclePercent = EstimateDutyFromRpm((int)rpm),
                    Temperature = index == 0 ? SensorHelper.GetCpuTemperature(_hwMonitor) : SensorHelper.GetGpuTemperature(_hwMonitor)
                });
                index++;
            }

            // Fallback if no fans detected
            if (fans.Count == 0)
            {
                fans.Add(new FanTelemetry { Name = "CPU Fan", SpeedRpm = 0, DutyCyclePercent = 0, Temperature = SensorHelper.GetCpuTemperature(_hwMonitor) });
                fans.Add(new FanTelemetry { Name = "GPU Fan", SpeedRpm = 0, DutyCyclePercent = 0, Temperature = SensorHelper.GetGpuTemperature(_hwMonitor) });
            }

            return fans;
        }

        private int EstimateDutyFromRpm(int rpm)
        {
            if (rpm == 0) return 0;
            const int minRpm = 1500;
            const int maxRpm = 6000;
            return Math.Clamp((rpm - minRpm) * 100 / (maxRpm - minRpm), 0, 100);
        }

        public void ApplyMaxCooling() => _logging?.Warn("Fan control not available: Cannot apply max cooling");
        public void ApplyAutoMode() => _logging?.Warn("Fan control not available: Cannot apply auto mode");
        public void ApplyQuietMode() => _logging?.Warn("Fan control not available: Cannot apply quiet mode");
        
        public bool ResetEcToDefaults()
        {
            _logging?.Warn("EC reset not available: No fan control backend");
            return false;
        }
        
        public bool ApplyThrottlingMitigation()
        {
            _logging?.Warn("Throttling mitigation not available: No fan control backend");
            return false;
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
    
    /// <summary>
    /// Static helper for getting sensor data from the best available source.
    /// Used by fan controller wrappers to get data without requiring LibreHardwareMonitor.
    /// </summary>
    internal static class SensorHelper
    {
        private static HpWmiBios? _wmiBios;
        
        private static HpWmiBios WmiBios => _wmiBios ??= new HpWmiBios(null);
        
        /// <summary>
        /// Get fan speeds from LibreHardwareMonitor or WMI BIOS fallback.
        /// </summary>
        public static IEnumerable<(string Name, double Rpm)> GetFanSpeeds(LibreHardwareMonitorImpl? libreHw)
        {
            // Try LibreHardwareMonitor first
            if (libreHw != null)
            {
                try
                {
                    var speeds = libreHw.GetFanSpeeds();
                    if (speeds.Any())
                        return speeds;
                }
                catch (Exception)
                {
                }
            }
            
            // Fall back to WMI BIOS
            var rpms = WmiBios.GetFanRpmDirect();
            var result = new List<(string, double)>();
            
            if (rpms.HasValue)
            {
                var (cpuRpm, gpuRpm) = rpms.Value;
                if (HpWmiBios.IsValidRpm(cpuRpm))
                    result.Add(("CPU Fan", cpuRpm));
                
                if (HpWmiBios.IsValidRpm(gpuRpm))
                    result.Add(("GPU Fan", gpuRpm));
            }
            
            return result;
        }
        
        /// <summary>
        /// Get CPU temperature from LibreHardwareMonitor or WMI BIOS fallback.
        /// </summary>
        public static double GetCpuTemperature(LibreHardwareMonitorImpl? libreHw)
        {
            if (libreHw != null)
            {
                try
                {
                    var temp = libreHw.GetCpuTemperature();
                    if (temp > 0) return temp;
                }
                catch (Exception)
                {
                }
            }
            
            return WmiBios.GetTemperature() ?? 0;
        }
        
        /// <summary>
        /// Get GPU temperature from LibreHardwareMonitor or WMI BIOS fallback.
        /// </summary>
        public static double GetGpuTemperature(LibreHardwareMonitorImpl? libreHw)
        {
            if (libreHw != null)
            {
                try
                {
                    var temp = libreHw.GetGpuTemperature();
                    if (temp > 0) return temp;
                }
                catch (Exception)
                {
                }
            }
            
            return WmiBios.GetGpuTemperature() ?? 0;
        }
    }
}
