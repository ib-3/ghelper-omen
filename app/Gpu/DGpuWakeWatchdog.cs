using GHelper.Gpu.NVidia;
using GHelper.Helpers;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GHelper.Gpu
{
    public static class DGpuWakeWatchdog
    {
        private static int consecutiveHits = 0;
        private static long lastRecoveryTime = 0;
        private static long lastPeriodicApplyTime = 0;
        
        private const int TRIGGER_THRESHOLD_WATTS = 10;
        private const int TRIGGER_CYCLES = 3;
        private const int COOLDOWN_MS = 60000; // 60 seconds
        private const int PERIODIC_REAPPLY_MS = 300000; // 5 minutes

        public static void Check()
        {
            // Only run if on battery, in Eco mode, and it's an OMEN device (since this is an OMEN-specific issue)
            if (GPUModeControl.IsPlugged() || !HardwareControl.IsEcoMode() || !Program.acpi.IsOmen())
            {
                consecutiveHits = 0;
                return;
            }

            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            // 1. Check Periodic Reapply
            if (lastPeriodicApplyTime > 0 && now - lastPeriodicApplyTime > PERIODIC_REAPPLY_MS)
            {
                Logger.WriteLine("[DGpuWakeWatchdog] Triggering periodic Eco reapply to ensure dGPU stays asleep.");
                lastPeriodicApplyTime = now;
                TriggerRecovery();
                return;
            }
            else if (lastPeriodicApplyTime == 0)
            {
                lastPeriodicApplyTime = now;
            }

            // 2. Check Active Power Drain
            if (HardwareControl.batteryRate < 0)
            {
                decimal dischargeWatts = -((decimal)HardwareControl.batteryRate);
                double pkgPowerWatts = Program.acpi?.GetCpuPackagePowerWatts() ?? 0.0;

                decimal systemPowerDraw = dischargeWatts - (decimal)pkgPowerWatts;

                if (systemPowerDraw > TRIGGER_THRESHOLD_WATTS)
                {
                    consecutiveHits++;
                    Logger.WriteLine($"[DGpuWakeWatchdog] High system power detected: {systemPowerDraw:F1}W (Discharge: {dischargeWatts:F1}W - Pkg: {pkgPowerWatts:F1}W). Hit {consecutiveHits}/{TRIGGER_CYCLES}");

                    if (consecutiveHits >= TRIGGER_CYCLES)
                    {
                        if (now - lastRecoveryTime > COOLDOWN_MS)
                        {
                            Logger.WriteLine("[DGpuWakeWatchdog] Threshold reached! Triggering recovery process.");
                            lastRecoveryTime = now;
                            lastPeriodicApplyTime = now; // Reset periodic timer too
                            TriggerRecovery();
                        }
                        else
                        {
                            Logger.WriteLine("[DGpuWakeWatchdog] Recovery in cooldown.");
                        }
                        consecutiveHits = 0;
                    }
                }
                else
                {
                    if (consecutiveHits > 0)
                    {
                        Logger.WriteLine($"[DGpuWakeWatchdog] Power draw returned to normal ({systemPowerDraw:F1}W). Resetting hits.");
                        consecutiveHits = 0;
                    }
                }
            }
        }

        private static void TriggerRecovery()
        {
            Task.Run(async () =>
            {
                try
                {
                    Logger.WriteLine("[DGpuWakeWatchdog] Killing GPU apps...");
                    HardwareControl.KillGPUApps();
                    
                    if (AppConfig.IsNVPlatform())
                    {
                        Logger.WriteLine("[DGpuWakeWatchdog] Stopping NV services...");
                        NvidiaGpuControl.StopNVService();
                        await Task.Delay(TimeSpan.FromMilliseconds(1000));
                    }
                    else
                    {
                        NvidiaGpuControl.FixNvContainer();
                    }

                    Logger.WriteLine("[DGpuWakeWatchdog] Re-applying Eco mode to BIOS...");
                    Program.acpi.SetGPUEco(1);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"[DGpuWakeWatchdog] Error during recovery: {ex.Message}");
                }
            });
        }
    }
}
