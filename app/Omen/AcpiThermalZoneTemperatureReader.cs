using System;
using System.Management;

namespace OmenCore.Hardware
{
    internal static class AcpiThermalZoneTemperatureReader
    {
        private static readonly object ReadLock = new();
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
        private static double _cachedCpuTemperature;
        private static DateTime _lastReadUtc = DateTime.MinValue;
        private static DateTime _disabledUntilUtc = DateTime.MinValue;
        private static string? _cachedInstanceName;

        public static double ConvertTenthsKelvinToCelsius(uint rawTemp)
        {
            if (rawTemp == 0)
            {
                return 0;
            }

            double tempC = Math.Round((rawTemp / 10.0) - 273.15, 1);
            return IsValidCpuTemperature(tempC) ? tempC : 0;
        }

        public static bool IsValidCpuTemperature(double tempC)
        {
            return tempC > 0 && tempC < 110;
        }

        public static double ReadCpuTemperature(ref string? preferredInstanceName)
        {
            var nowUtc = DateTime.UtcNow;
            lock (ReadLock)
            {
                if (nowUtc < _disabledUntilUtc)
                {
                    return 0;
                }

                if (_cachedCpuTemperature > 0 && nowUtc - _lastReadUtc < CacheLifetime)
                {
                    if (preferredInstanceName == null)
                    {
                        preferredInstanceName = _cachedInstanceName;
                    }
                    return _cachedCpuTemperature;
                }

                double temp = ReadCpuTemperatureUncached(ref preferredInstanceName);
                _lastReadUtc = nowUtc;
                if (temp > 0)
                {
                    _cachedCpuTemperature = temp;
                    _cachedInstanceName = preferredInstanceName;
                    return temp;
                }

                _cachedCpuTemperature = 0;
                _disabledUntilUtc = nowUtc.Add(FailureCooldown);
                return 0;
            }
        }

        private static double ReadCpuTemperatureUncached(ref string? preferredInstanceName)
        {
            // Try the non-elevated CIMV2 PerfCounter query first — works without admin
            double temp = ReadViaPerfCounter();
            if (temp > 0)
            {
                return temp;
            }

            // Fallback: ACPI thermal zone (requires admin / root\wmi access)
            return ReadViaAcpiThermalZone(ref preferredInstanceName);
        }

        /// <summary>
        /// Read CPU temperature via Win32_PerfFormattedData_Counters_ThermalZoneInformation.
        /// This works without elevation. Temperature is reported in Kelvin.
        /// 
        /// IMPORTANT: On HP Omen laptops, this WMI class exposes multiple thermal zones
        /// including chassis/skin zones (e.g. ~39°C) that are NOT the CPU package.
        /// We must filter by instance name to avoid reading the wrong zone.
        /// If no CPU-like zone is found, return 0 to fall through to the ACPI/WMI BIOS path.
        /// </summary>
        private static double ReadViaPerfCounter()
        {
            try
            {
                // Include Name so we can filter out non-CPU thermal zones.
                // On HP Omen, the first zone returned is often a skin/chassis sensor (~39°C),
                // not the CPU package. Without filtering, this causes a permanently wrong readout.
                using var searcher = new ManagementObjectSearcher(
                    @"root\CIMV2",
                    "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

                double bestTemp = 0;
                bool foundCpuLikeZone = false;

                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["Temperature"] is not uint kelvin || kelvin == 0)
                        continue;

                    double tempC = Math.Round(kelvin - 273.15, 1);
                    var instanceName = obj["Name"]?.ToString() ?? string.Empty;
                    bool isCpuLike = IsCpuLikeInstance(instanceName);

                    global::Logger.WriteLine($"[AcpiReader] PerfCounter Zone: Name='{instanceName}', Temp={tempC}°C, Valid={IsValidCpuTemperature(tempC)}, CpuLike={isCpuLike}");

                    if (!IsValidCpuTemperature(tempC))
                        continue;

                    if (isCpuLike)
                    {
                        // Prefer the highest reading among CPU-like zones
                        // (package temp > core temp > ambient zone)
                        if (!foundCpuLikeZone || tempC > bestTemp)
                        {
                            bestTemp = tempC;
                            foundCpuLikeZone = true;
                        }
                    }
                    else if (!foundCpuLikeZone && bestTemp == 0)
                    {
                        // Keep as tentative fallback only if we never find a CPU-like zone
                        bestTemp = tempC;
                    }
                }

                // Only accept the result if a CPU-like zone was found.
                // If only generic/ambient zones exist, return 0 to let the ACPI WMI path try.
                return foundCpuLikeZone ? bestTemp : 0;
            }
            catch (Exception ex)
            {
                global::Logger.WriteLine($"[AcpiReader] ReadViaPerfCounter failed: {ex.Message}");
                // Not available on this system — fall through to ACPI fallback
            }

            return 0;
        }

        /// <summary>
        /// Read CPU temperature via MSAcpi_ThermalZoneTemperature (root\wmi).
        /// Requires admin elevation. Temperature is in tenths of Kelvin.
        /// </summary>
        private static double ReadViaAcpiThermalZone(ref string? preferredInstanceName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");

                double bestTemp = 0;

                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["CurrentTemperature"] is not uint rawTemp)
                    {
                        continue;
                    }

                    double tempC = ConvertTenthsKelvinToCelsius(rawTemp);
                    if (tempC <= 0)
                    {
                        continue;
                    }

                    var instanceName = obj["InstanceName"]?.ToString() ?? "";
                    bool isCpuLike = IsCpuLikeInstance(instanceName);

                    global::Logger.WriteLine($"[AcpiReader] ACPI Zone: InstanceName='{instanceName}', Temp={tempC}°C, Valid={IsValidCpuTemperature(tempC)}, CpuLike={isCpuLike}");

                    if (!IsValidCpuTemperature(tempC))
                    {
                        continue;
                    }

                    if (preferredInstanceName == null)
                    {
                        bestTemp = tempC;
                        preferredInstanceName = instanceName;
                    }
                    else if (instanceName == preferredInstanceName)
                    {
                        bestTemp = tempC;
                    }
                    else if (IsCpuLikeInstance(instanceName))
                    {
                        bestTemp = tempC;
                        preferredInstanceName = instanceName;
                    }
                    else if (bestTemp == 0)
                    {
                        bestTemp = tempC;
                    }
                }

                return bestTemp;
            }
            catch (Exception ex)
            {
                global::Logger.WriteLine($"[AcpiReader] ReadViaAcpiThermalZone failed: {ex.Message}");
                return 0;
            }
        }

        private static bool IsCpuLikeInstance(string instanceName)
        {
            // Match known CPU thermal zone naming conventions across vendors.
            // Deliberately excludes generic names like "_TZ.THRM" or "Skin" that appear
            // on HP Omen laptops as chassis/ambient sensors rather than CPU package temps.
            return instanceName.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                   instanceName.Contains("CPUZ", StringComparison.OrdinalIgnoreCase) ||
                   instanceName.Contains("TZ00", StringComparison.OrdinalIgnoreCase) ||
                   instanceName.Contains("PROC", StringComparison.OrdinalIgnoreCase) ||
                   instanceName.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                   instanceName.Contains("Package", StringComparison.OrdinalIgnoreCase);
        }
    }
}
