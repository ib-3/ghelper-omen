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
        /// If no CPU-like zone is found, fall back to the hottest valid zone
        /// (CPU is always the hottest active thermal zone on a running laptop).
        /// </summary>
        private static double ReadViaPerfCounter()
        {
            try
            {
                // Include Name so we can filter out non-CPU thermal zones.
                // On HP Omen/Victus, the first zone returned is often a skin/chassis sensor (~20°C),
                // not the CPU package. Without filtering, this causes a permanently wrong readout.
                //
                // HP Ryzen-specific note: the CPU package zone is typically named "TSZ0" on
                // HP Victus/Omen Ryzen laptops (confirmed on Ryzen 5 7640HS, BIOS F.31).
                // "TSZ0" does NOT match any of the IsCpuLikeInstance() patterns, so without the
                // highest-temp fallback below, Ryzen CPU temp would be reported as 0 or as the
                // ambient zone's 20°C.
                using var searcher = new ManagementObjectSearcher(
                    @"root\CIMV2",
                    "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

                double bestTemp = 0;
                double bestNonCpuLikeTemp = 0; // fallback: hottest valid zone
                bool foundCpuLikeZone = false;

                foreach (ManagementObject obj in searcher.Get())
                {
                    // Prefer HighPrecisionTemperature (tenths of Kelvin) when available;
                    // the integer "Temperature" field loses precision and can round 52°C down to 50°C.
                    double tempC = 0;
                    if (obj["HighPrecisionTemperature"] is uint hpTemp && hpTemp > 0)
                    {
                        tempC = Math.Round((hpTemp / 10.0) - 273.15, 1);
                    }
                    else if (obj["Temperature"] is uint kelvin && kelvin > 0)
                    {
                        tempC = Math.Round(kelvin - 273.15, 1);
                    }

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
                    else
                    {
                        // Track the hottest valid non-CPU-like zone as a fallback.
                        // On HP Ryzen systems where the CPU zone is named "TSZ0" (not CPU-like),
                        // this is what actually returns the real CPU temperature — the CPU is
                        // always the hottest active thermal zone on a running laptop.
                        if (tempC > bestNonCpuLikeTemp)
                        {
                            bestNonCpuLikeTemp = tempC;
                        }
                    }
                }

                if (foundCpuLikeZone)
                    return bestTemp;

                // No CPU-named zone found (common on HP Ryzen). Use the hottest valid zone
                // as the CPU temp — the CPU is always hotter than ambient/skin sensors.
                // Log it so we can see which zone we're attributing to the CPU.
                if (bestNonCpuLikeTemp > 0)
                {
                    global::Logger.WriteLine($"[AcpiReader] No CPU-named zone found — using hottest valid zone ({bestNonCpuLikeTemp}°C) as CPU temp (HP Ryzen fallback)");
                    return bestNonCpuLikeTemp;
                }

                return 0;
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

                double bestCpuLikeTemp = 0;
                string? bestCpuLikeInstance = null;
                double bestNonCpuLikeTemp = 0; // fallback: hottest valid zone
                string? bestNonCpuLikeInstance = null;

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

                    // If we already have a preferred instance (from a previous call), trust it
                    // as long as it's still reporting valid data.
                    if (preferredInstanceName != null && instanceName == preferredInstanceName)
                    {
                        bestCpuLikeTemp = tempC;
                        bestCpuLikeInstance = instanceName;
                        // Keep scanning in case a CPU-like zone shows up with higher confidence,
                        // but a sticky preferredInstance is a strong signal.
                    }

                    if (isCpuLike)
                    {
                        if (tempC > bestCpuLikeTemp)
                        {
                            bestCpuLikeTemp = tempC;
                            bestCpuLikeInstance = instanceName;
                        }
                    }
                    else
                    {
                        // Track the hottest valid non-CPU-like zone.
                        // On HP Ryzen laptops the CPU zone is named "TSZ0" (not CPU-like), so this
                        // branch is what actually captures the real CPU temp. The CPU is always
                        // hotter than ambient/skin sensors on a running laptop.
                        if (tempC > bestNonCpuLikeTemp)
                        {
                            bestNonCpuLikeTemp = tempC;
                            bestNonCpuLikeInstance = instanceName;
                        }
                    }
                }

                if (bestCpuLikeTemp > 0)
                {
                    preferredInstanceName = bestCpuLikeInstance;
                    return bestCpuLikeTemp;
                }

                if (bestNonCpuLikeTemp > 0)
                {
                    global::Logger.WriteLine($"[AcpiReader] No CPU-named ACPI zone found — using hottest valid zone '{bestNonCpuLikeInstance}' ({bestNonCpuLikeTemp}°C) as CPU temp (HP Ryzen fallback)");
                    preferredInstanceName = bestNonCpuLikeInstance;
                    return bestNonCpuLikeTemp;
                }

                return 0;
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
