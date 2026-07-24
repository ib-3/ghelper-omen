using System;
using System.Collections.Generic;
using System.Linq;
using OmenCore.Models;
using OmenCore.Services;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Interface for fan control operations.
    /// Implemented by both WMI-based and EC-based controllers.
    /// </summary>
    public interface IFanController : IDisposable
    {
        bool IsAvailable { get; }
        string Status { get; }
        string Backend { get; }

        /// <summary>
        /// True when the backend is actively maintaining fan ownership (for example a
        /// keepalive/countdown extension) even if no custom curve is running.
        /// </summary>
        bool IsHoldActive => false;

        bool ApplyPreset(FanPreset preset);
        bool ApplyCustomCurve(IEnumerable<FanCurvePoint> curve);
        bool SetFanSpeed(int percent);
        bool SetFanSpeeds(int cpuPercent, int gpuPercent);
        bool? GetMaxFanSpeed();
        bool SetMaxFanSpeed(bool enabled);
        bool SetPerformanceMode(string modeName);
        bool RestoreAutoControl();
        IEnumerable<FanTelemetry> ReadFanSpeeds();

        // Quick profile methods
        void ApplyMaxCooling();
        void ApplyAutoMode();
        void ApplyQuietMode();

        /// <summary>
        /// Reset EC (Embedded Controller) to factory defaults.
        /// Restores BIOS control of fans and clears all manual overrides.
        /// Use this to fix stuck fan readings or restore normal BIOS display values.
        /// </summary>
        bool ResetEcToDefaults();

        // Verify that Max fan speed was applied successfully.
        // Returns true if verification succeeded; "details" contains a short diagnostic description.
        bool VerifyMaxApplied(out string details);
        
        /// <summary>
        /// Apply performance throttling mitigation via EC register 0x95.
        /// Discovered from omen-fan Linux utility - writing 0x31 to this register
        /// can help mitigate thermal throttling on some OMEN models.
        /// </summary>
        /// <returns>True if the mitigation was applied successfully.</returns>
        bool ApplyThrottlingMitigation();
    }

    /// <summary>
    /// Wrapper for WmiFanController that implements IFanController.
    /// </summary>
    public class WmiFanControllerWrapper : IFanController
    {
        public bool VerifyMaxApplied(out string details)
        {
            // WMI may not expose RPM directly; check hardware monitor first
            // Use controller's ReadFanSpeeds (which may read WMI or HWMonitor internally)
            try
            {
                var speeds = _controller.ReadFanSpeeds().ToList();
                if (speeds.Any())
                {
                    details = $"ReadFanSpeeds: {string.Join(',', speeds.Select(s => s.SpeedRpm))}";
                    return speeds.Any(s => s.SpeedRpm > 1000);
                }
            }
            catch (Exception ex)
            {
                details = $"Verify attempt failed: {ex.Message}";
                return false;
            }

            details = "No RPMs available via WMI or HWMonitor.";
            return false;
        }

        private readonly WmiFanController _controller;
        private readonly LoggingService? _logging;

        public WmiFanControllerWrapper(WmiFanController controller, LoggingService? logging = null)
        {
            _controller = controller;
            _logging = logging;
        }

        public bool IsAvailable => _controller.IsAvailable;
        public string Status => _controller.Status;
        public string Backend => "WMI BIOS";
        public bool IsHoldActive => _controller.CountdownExtensionEnabled;
        
        /// <summary>
        /// Check if WMI commands are ineffective on this model.
        /// Some newer OMEN models (Transcend, 2024+) return success but don't change fan speed.
        /// </summary>
        public bool CommandsIneffective => _controller.CommandsIneffective;

        public DateTime? LastMaxModeExternalResetUtc => _controller.LastMaxModeExternalResetUtc;
        public string LastMaxModeExternalResetDetails => _controller.LastMaxModeExternalResetDetails;
        
        /// <summary>
        /// Test if WMI commands actually affect fan behavior.
        /// Returns true if commands appear to work, false if they seem ineffective.
        /// </summary>
        public bool TestCommandEffectiveness() => _controller.TestCommandEffectiveness();

        public bool ApplyPreset(FanPreset preset) => _controller.ApplyPreset(preset);
        public bool ApplyCustomCurve(IEnumerable<FanCurvePoint> curve) => _controller.ApplyCustomCurve(curve);
        public bool SetFanSpeed(int percent) => _controller.SetFanSpeed(percent);
        public bool SetFanSpeeds(int cpuPercent, int gpuPercent) => _controller.SetFanSpeeds(cpuPercent, gpuPercent);
        public bool? GetMaxFanSpeed() => _controller.GetMaxFanSpeed();
        public bool SetMaxFanSpeed(bool enabled) => _controller.SetMaxFanSpeed(enabled);
        public bool SetPerformanceMode(string modeName) => _controller.SetPerformanceMode(modeName);
        public bool RestoreAutoControl() => _controller.RestoreAutoControl();
        public IEnumerable<FanTelemetry> ReadFanSpeeds() => _controller.ReadFanSpeeds();

        public void ApplyMaxCooling() => _controller.SetMaxFanSpeed(true);
        public void ApplyAutoMode() => _controller.RestoreAutoControl();
        public void ApplyQuietMode() => _controller.SetPerformanceMode("Cool");
        
        public bool ResetEcToDefaults()
        {
            _logging?.Info("Resetting EC to defaults via WMI BIOS...");
            return _controller.ResetEcToDefaults();
        }
        
        public bool ApplyThrottlingMitigation() => _controller.ApplyThrottlingMitigation();

        public void Dispose() => _controller.Dispose();
    }
}
