using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PawnIO;

namespace OmenCore.Hardware
{
    /// <summary>
    /// PawnIO-based MSR access provider for Secure Boot compatible systems.
    /// Uses the signed PawnIO driver with IntelMSR module.
    /// This is the recommended MSR backend for v1.5+.
    /// </summary>
    public sealed class PawnIOMsrAccess : IMsrAccess
    {
        private IntPtr _handle = IntPtr.Zero;
        private IntPtr _pawnIOLib = IntPtr.Zero;
        private bool _moduleLoaded;
        private bool _disposed;

        // Embedded IntelMSR module binary
        private static byte[]? _intelMsrModule;

        // Function delegates
        private delegate int PawnioOpen(out IntPtr handle);
        private delegate int PawnioLoad(IntPtr handle, byte[] blob, IntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int PawnioExecute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string command, ulong[] input, IntPtr inputSize, ulong[] output, IntPtr outputSize, out IntPtr returnSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PawnioClose(IntPtr handle);


        public IntPtr Handle => _handle;
        
        private PawnioOpen? _pawnioOpen;
        private PawnioLoad? _pawnioLoad;
        private PawnioExecute? _pawnioExecute;
        
        public PawnioExecute? ExecuteDelegate => _pawnioExecute;
        private PawnioClose? _pawnioClose;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

        public bool IsAvailable => _handle != IntPtr.Zero && _moduleLoaded;

        public PawnIOMsrAccess()
        {
            Initialize();
        }

        private bool Initialize()
        {
            try
            {
                // Try bundled PawnIOLib.dll first
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string bundledLibPath = Path.Combine(appDir, "drivers", "PawnIOLib.dll");
                string? libPath = null;
                
                if (File.Exists(bundledLibPath))
                {
                    libPath = bundledLibPath;
                }
                else
                {
                    // Fall back to PawnIO installation
                    string? pawnIOPath = FindPawnIOInstallation();
                    if (pawnIOPath != null)
                    {
                        string installedLibPath = Path.Combine(pawnIOPath, "PawnIOLib.dll");
                        if (File.Exists(installedLibPath))
                        {
                            libPath = installedLibPath;
                        }
                    }
                }
                
                if (libPath == null) return false;

                _pawnIOLib = NativeMethods.LoadLibrary(libPath);
                if (_pawnIOLib == IntPtr.Zero) return false;

                // Resolve functions
                if (!ResolveFunctions()) return false;

                // Open PawnIO handle
                int hr = _pawnioOpen!(out _handle);
                if (hr < 0 || _handle == IntPtr.Zero) return false;

                // Load IntelMSR module
                if (!LoadMsrModule())
                {
                    _pawnioClose!(_handle);
                    _handle = IntPtr.Zero;
                    return false;
                }

                _moduleLoaded = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string? FindPawnIOInstallation()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key != null)
                {
                    string? installLocation = key.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                    {
                        return installLocation;
                    }
                }
            }
            catch { }

            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
            if (Directory.Exists(defaultPath)) return defaultPath;

            return null;
        }

        private bool ResolveFunctions()
        {
            IntPtr openPtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_open");
            IntPtr loadPtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_load");
            IntPtr executePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_execute");
            IntPtr closePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_close");

            if (openPtr == IntPtr.Zero || loadPtr == IntPtr.Zero || 
                executePtr == IntPtr.Zero || closePtr == IntPtr.Zero)
            {
                return false;
            }

            _pawnioOpen = Marshal.GetDelegateForFunctionPointer<PawnioOpen>(openPtr);
            _pawnioLoad = Marshal.GetDelegateForFunctionPointer<PawnioLoad>(loadPtr);
            _pawnioExecute = Marshal.GetDelegateForFunctionPointer<PawnioExecute>(executePtr);
            _pawnioClose = Marshal.GetDelegateForFunctionPointer<PawnioClose>(closePtr);

            return true;
        }

        private bool LoadMsrModule()
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] moduleNames = { "IntelMSR.bin", "IntelMSR.amx" };
                
                foreach (var moduleName in moduleNames)
                {
                    string modulePath = Path.Combine(appDir, "drivers", moduleName);
                    if (File.Exists(modulePath))
                    {
                        _intelMsrModule = File.ReadAllBytes(modulePath);
                        break;
                    }
                }

                if (_intelMsrModule == null || _intelMsrModule.Length == 0)
                {
                    string? pawnIOPath = FindPawnIOInstallation();
                    if (pawnIOPath != null)
                    {
                        foreach (var moduleName in moduleNames)
                        {
                            string installedModule = Path.Combine(pawnIOPath, "modules", moduleName);
                            if (File.Exists(installedModule))
                            {
                                _intelMsrModule = File.ReadAllBytes(installedModule);
                                break;
                            }
                        }
                    }
                }

                if (_intelMsrModule == null || _intelMsrModule.Length == 0) return false;

                int hr = _pawnioLoad!(_handle, _intelMsrModule, (IntPtr)_intelMsrModule.Length);
                return hr >= 0;
            }
            catch
            {
                return false;
            }
        }

        public bool LoadAdditionalModule(byte[] blob)
        {
            if (!IsAvailable) return false;
            try
            {
                int hr = _pawnioLoad!(_handle, blob, (IntPtr)blob.Length);
                if (hr < 0)
                {
                    Logger.WriteLine($"[PawnIOMsrAccess] pawnio_load failed: HRESULT 0x{hr:X8} "
                                     + "(module may be unsigned or for wrong PawnIO version)");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[PawnIOMsrAccess] LoadAdditionalModule exception: {ex.Message}");
                return false;
            }
        }

        public int Execute(string command, ulong[] input, ulong[] output)
        {
            if (!IsAvailable)
                throw new InvalidOperationException("PawnIO not available");

            int hr = _pawnioExecute!(_handle, command,
                                      input,  (IntPtr)input.Length,
                                      output, (IntPtr)output.Length,
                                      out IntPtr returnSize);
            return hr;
        }

        public void ApplyCoreVoltageOffset(int offsetMv)
        {
            // Safety clamp: Intel undervolting safe range is -250 mV to 0 mV
            offsetMv = Math.Clamp(offsetMv, -250, 0);
            // MSR 0x150 - IA32_VOLTAGE_PLANE_0 (Core)
            WriteVoltageOffset(0x150, offsetMv);
        }

        public void ApplyCacheVoltageOffset(int offsetMv)
        {
            // Safety clamp: Intel undervolting safe range is -250 mV to 0 mV
            offsetMv = Math.Clamp(offsetMv, -250, 0);
            // MSR 0x152 - IA32_VOLTAGE_PLANE_2 (Cache)
            WriteVoltageOffset(0x152, offsetMv);
        }

        public int ReadCoreVoltageOffset()
        {
            return ReadVoltageOffset(0x150);
        }

        public int ReadCacheVoltageOffset()
        {
            return ReadVoltageOffset(0x152);
        }

        private void WriteVoltageOffset(uint msr, int offsetMv)
        {
            EnsureAvailable();
            
            // Convert mV to MSR format (same logic as WinRing0MsrAccess)
            // This logic should be shared, but for now duplicating to avoid refactoring everything
            long offsetVal = (long)Math.Round(offsetMv * 1.024);
            ulong value = 0x8000001100000000; // Write command + 1.024 scale
            
            if (offsetVal < 0)
            {
                value |= (ulong)((0xFFE00000 + offsetVal) & 0xFFF00000); // Negative
            }
            else
            {
                value |= (ulong)(offsetVal & 0xFFF00000); // Positive
            }

            WriteMsr(msr, value);
        }

        private int ReadVoltageOffset(uint msr)
        {
            EnsureAvailable();
            
            try
            {
                ulong value = ReadMsr(msr);
                long offsetUnits = (long)((value >> 21) & 0x7FF);
                
                // Sign extend from 11 bits
                if ((offsetUnits & 0x400) != 0)
                {
                    offsetUnits |= unchecked((long)0xFFFFFFFFFFFFF800);
                }
                
                return (int)Math.Round(offsetUnits / 1.024);
            }
            catch
            {
                return 0;
            }
        }

        public ulong ReadRawMsr(uint msr)
        {
            return ReadMsr(msr);
        }
        private ulong ReadMsr(uint index)
        {
            ulong[] input = { index };
            ulong[] output = new ulong[1]; // Pawn module returns 1 cell (32-bit value)

            // DEFINE_IOCTL_SIZED(ioctl_read_msr, 1, 1) — in_size=1, out_size=1
            int hr = _pawnioExecute!(_handle, "ioctl_read_msr", input, (IntPtr)1, output, (IntPtr)1, out IntPtr returnSize);
            if (hr < 0)
            {
                throw new InvalidOperationException($"PawnIO MSR read failed: HRESULT 0x{hr:X8}");
            }

            // Pawn VM uses 32-bit cells — msr_read() returns only the low 32 bits.
            // For 64-bit MSRs (e.g. 0x610 PKG_POWER_LIMIT), the high 32 bits are lost.
            // Use the MMIO path for full 64-bit power limit access.
            return output[0] & 0xFFFFFFFFUL;
        }

        private void WriteMsr(uint index, ulong value)
        {
            EnsureAvailable();

            // DEFINE_IOCTL_SIZED(ioctl_write_msr, 2, 0) — in[0]=MSR, in[1]=value
            // Pawn VM uses 32-bit cells, so only the low 32 bits of 'value' are written.
            // For 64-bit MSRs (e.g. 0x610), the high 32 bits are silently dropped.
            // Use the MMIO path for full 64-bit power limit writes.
            if ((value >> 32) != 0)
            {
                Logger.WriteLine($"[MSR] WARNING: WriteMsr(0x{index:X}) value 0x{value:X16} has non-zero high bits — "
                                 + "Pawn module only writes low 32 bits. Use MMIO path for 64-bit MSR writes.");
            }

            ulong low = value & 0xFFFFFFFF;
            ulong[] input = { index, low };
            ulong[] output = Array.Empty<ulong>();

            int hr = _pawnioExecute!(_handle, "ioctl_write_msr",
                                      input,  (IntPtr)2,
                                      output, IntPtr.Zero,
                                      out IntPtr returnSize);
            if (hr < 0)
                throw new InvalidOperationException($"PawnIO MSR write failed: HRESULT 0x{hr:X8}");
        }

        private void EnsureAvailable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PawnIOMsrAccess));
            if (!IsAvailable) throw new InvalidOperationException("PawnIO MSR access is not available");
        }


        private static IEnumerable<ulong> GetWritableCpuAffinityMasks()
        {
            ulong processAffinity = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64();
            int maxBits = IntPtr.Size * 8;
            int cpuCount = Math.Min(Environment.ProcessorCount, maxBits);

            for (int cpu = 0; cpu < cpuCount; cpu++)
            {
                ulong mask = 1UL << cpu;
                if ((processAffinity & mask) != 0)
                    yield return mask;
            }
        }

        private bool ExecuteOnCpu(ulong affinityMask, Action action)
        {
            Thread.BeginThreadAffinity();
            UIntPtr previous = UIntPtr.Zero;
            try
            {
                previous = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(affinityMask));
                if (previous == UIntPtr.Zero) return false;

                Thread.Yield();
                action();
                return true;
            }
            finally
            {
                if (previous != UIntPtr.Zero)
                    SetThreadAffinityMask(GetCurrentThread(), previous);
                Thread.EndThreadAffinity();
            }
        }

        // ==========================================
        // Intel Speed Shift / HWP EPP
        // ==========================================

        private const uint MSR_IA32_HWP_REQUEST = 0x774;
        private const ulong HWP_EPP_MASK = 0xFFUL << 24;

        public int ReadHwpEpp()
        {
            EnsureAvailable();
            ulong hwpRequest = ReadMsr(MSR_IA32_HWP_REQUEST);
            return (int)((hwpRequest >> 24) & 0xFF);
        }

        public bool SetHwpEpp(int epp)
        {
            EnsureAvailable();
            epp = Math.Clamp(epp, 0, 255);

            int attempted = 0;
            int verified = 0;

            foreach (ulong mask in GetWritableCpuAffinityMasks())
            {
                bool ok = ExecuteOnCpu(mask, () =>
                {
                    ulong current = ReadMsr(MSR_IA32_HWP_REQUEST);
                    ulong updated = (current & ~HWP_EPP_MASK) | ((ulong)epp << 24);
                    WriteMsr(MSR_IA32_HWP_REQUEST, updated);

                    ulong verify = ReadMsr(MSR_IA32_HWP_REQUEST);
                    if (((verify >> 24) & 0xFF) != (ulong)epp)
                        throw new InvalidOperationException("HWP EPP verification failed");
                });

                attempted++;
                if (ok) verified++;
            }

            return attempted > 0 && attempted == verified;
        }

        // ==========================================
        // TCC Offset (Thermal Control Circuit)
        // ==========================================
        
        /// <summary>
        /// MSR 0x1A2 - IA32_TEMPERATURE_TARGET
        /// Bits 29:24 contain the TCC activation temperature offset (0-63°C reduction)
        /// </summary>
        private const uint MSR_IA32_TEMPERATURE_TARGET = 0x1A2;
        
        /// <summary>
        /// Read the current TCC offset (temperature limit reduction).
        /// Returns 0-63, where 0 = no limit, 63 = max 63°C below TjMax.
        /// </summary>
        public int ReadTccOffset()
        {
            EnsureAvailable();
            try
            {
                ulong value = ReadMsr(MSR_IA32_TEMPERATURE_TARGET);
                // Bits 29:24 contain the TCC offset
                int offset = (int)((value >> 24) & 0x3F);
                return offset;
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Read the TjMax (maximum junction temperature) from MSR.
        /// This is the base temperature before TCC offset is applied.
        /// </summary>
        public int ReadTjMax()
        {
            EnsureAvailable();
            try
            {
                ulong value = ReadMsr(MSR_IA32_TEMPERATURE_TARGET);
                // Bits 23:16 contain TjMax
                int tjMax = (int)((value >> 16) & 0xFF);
                return tjMax > 0 ? tjMax : 100; // Default to 100°C if not readable
            }
            catch
            {
                return 100; // Default TjMax
            }
        }
        
        /// <summary>
        /// Set the TCC offset to limit maximum CPU temperature.
        /// Offset of N means CPU will throttle at (TjMax - N)°C.
        /// </summary>
        /// <param name="offset">Offset in degrees (0-63). 0 = no limit, 15 = throttle 15°C below TjMax</param>
        public void SetTccOffset(int offset)
        {
            EnsureAvailable();
            if (offset < 0 || offset > 63)
            {
                throw new ArgumentException("TCC offset must be between 0 and 63");
            }
            
            // Read current value to preserve other bits
            ulong currentValue = ReadMsr(MSR_IA32_TEMPERATURE_TARGET);
            
            // Clear bits 29:24 and set new offset
            ulong newValue = (currentValue & ~(0x3FUL << 24)) | ((ulong)offset << 24);
            
            WriteMsr(MSR_IA32_TEMPERATURE_TARGET, newValue);
        }
        
        /// <summary>
        /// Get the effective temperature limit (TjMax - TCC offset).
        /// </summary>
        public int GetEffectiveTempLimit()
        {
            int tjMax = ReadTjMax();
            int offset = ReadTccOffset();
            return tjMax - offset;
        }
        
        // ==========================================
        // Throttling Detection (EDP)
        // ==========================================
        
        /// <summary>
        /// Read CPU thermal throttling status from MSR.
        /// Returns true if CPU is thermally throttling.
        /// </summary>
        public bool ReadThermalThrottlingStatus()
        {
            if (!IsAvailable) return false;
            
            try
            {
                // MSR 0x19C: IA32_THERM_STATUS
                // Bit 0: Thermal Status (1 = thermal throttling active)
                // Bit 1: Thermal Status Log
                // Bit 2: PROCHOT or ForcePR Status
                // Bit 3: PROCHOT or ForcePR Status Log
                // Bit 4: Critical Temperature Status
                // Bit 5: Critical Temperature Status Log
                // Bit 6: Thermal Threshold #1 Status
                // Bit 7: Thermal Threshold #1 Status Log
                // Bit 8: Thermal Threshold #2 Status
                // Bit 9: Thermal Threshold #2 Status Log
                // Bit 10: Power Limit Status
                // Bit 11: Power Limit Status Log
                // Bit 12: Current Limit Status
                // Bit 13: Current Limit Status Log
                // Bit 14: Cross Domain Limit Status
                // Bit 15: Cross Domain Limit Status Log
                ulong status = ReadMsr(0x19C);
                return (status & 0x1) != 0; // Bit 0: Thermal Status
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Read CPU power throttling status from MSR.
        /// Returns true if CPU is power limit throttling.
        /// </summary>
        public bool ReadPowerThrottlingStatus()
        {
            if (!IsAvailable) return false;
            
            try
            {
                // MSR 0x19C: IA32_THERM_STATUS
                // Bit 10: Power Limit Status (1 = power limit throttling active)
                ulong status = ReadMsr(0x19C);
                return (status & (1UL << 10)) != 0;
            }
            catch
            {
                return false;
            }
        }
        
        // ==========================================
        // Power Limit Control (EDP Override)
        // ==========================================
        
        // MSR 0x610 bit definitions (MSR_PKG_POWER_LIMIT)
        private const uint MSR_PKG_POWER_LIMIT_ADDR = 0x610;
        private const ulong PL1_POWER_MASK = 0x7FFF;        // Bits 14:0  - Power limit in 1/8W units
        private const ulong PL1_ENABLE_BIT = 1UL << 15;     // Bit 15    - PL1 enable
        private const ulong PL1_CLAMP_BIT = 1UL << 16;      // Bit 16    - PL1 clamp (allow throttling below OS-requested P-state)
        private const ulong PL1_TIME_MASK = 0x7FUL << 17;   // Bits 23:17 - Time window exponent
        private const ulong PL2_POWER_MASK = 0x7FFFUL << 32; // Bits 46:32 - PL2 power limit
        private const ulong PL2_ENABLE_BIT = 1UL << 47;     // Bit 47    - PL2 enable
        private const ulong PL2_CLAMP_BIT = 1UL << 48;      // Bit 48    - PL2 clamp
        private const ulong PL2_TIME_MASK = 0x7FUL << 49;   // Bits 55:49 - PL2 time window
        private const ulong LOCK_BIT = 1UL << 63;           // Bit 63    - Lock (read-only once set)

        /// <summary>
        /// Check if power limit MSR is locked by BIOS (cannot be modified until next reboot)
        /// </summary>
        public bool IsPowerLimitLocked()
        {
            if (!IsAvailable) return true;
            
            try
            {
                ulong limit = ReadMsr(MSR_PKG_POWER_LIMIT_ADDR);
                return (limit & LOCK_BIT) != 0;
            }
            catch
            {
                return true; // Assume locked if we can't read
            }
        }
        
        /// <summary>
        /// Get detailed power limit status including PL1, PL2, enable states, and lock status
        /// </summary>
        public (double Pl1Watts, double Pl2Watts, bool Pl1Enabled, bool Pl2Enabled, bool IsLocked) GetPowerLimitStatus()
        {
            if (!IsAvailable) return (0, 0, false, false, true);
            
            try
            {
                ulong limit = ReadMsr(MSR_PKG_POWER_LIMIT_ADDR);
                
                double pl1 = (limit & PL1_POWER_MASK) / 8.0;
                double pl2 = ((limit >> 32) & 0x7FFF) / 8.0;
                bool pl1Enabled = (limit & PL1_ENABLE_BIT) != 0;
                bool pl2Enabled = (limit & PL2_ENABLE_BIT) != 0;
                bool locked = (limit & LOCK_BIT) != 0;
                
                return (pl1, pl2, pl1Enabled, pl2Enabled, locked);
            }
            catch
            {
                return (0, 0, false, false, true);
            }
        }
        
        /// <summary>
        /// Set both PL1 and PL2 power limits at once (more efficient than separate calls).
        /// Only works if power limits are not locked by BIOS.
        /// </summary>
        /// <param name="pl1Watts">Sustained power limit (PL1) in watts</param>
        /// <param name="pl2Watts">Burst power limit (PL2) in watts</param>
        /// <returns>True if successfully set, false if locked or failed</returns>
        public bool SetPowerLimits(double pl1Watts, double pl2Watts)
        {
            if (!IsAvailable) return false;
            
            try
            {
                // Read current value
                ulong current = ReadMsr(MSR_PKG_POWER_LIMIT_ADDR);
                
                // Convert watts to 1/8 watt units
                uint pl1 = (uint)Math.Clamp(pl1Watts * 8, 0, 0x7FFF);
                uint pl2 = (uint)Math.Clamp(pl2Watts * 8, 0, 0x7FFF);
                
                // Build new value preserving time windows but updating power and enable bits
                // Do NOT set the lock bit — ThrottleStop never sets it either
                ulong newValue = current & ~LOCK_BIT; // Clear lock bit in our write
                
                // Clear and set PL1 (bits 14:0, enable bit 15, and clamp bit 16)
                newValue = (newValue & ~(PL1_POWER_MASK | PL1_ENABLE_BIT | PL1_CLAMP_BIT)) | pl1 | PL1_ENABLE_BIT | PL1_CLAMP_BIT;
                
                // Clear and set PL2 (bits 46:32, enable bit 47, and clamp bit 48)
                newValue = (newValue & ~(PL2_POWER_MASK | PL2_ENABLE_BIT | PL2_CLAMP_BIT)) | ((ulong)pl2 << 32) | PL2_ENABLE_BIT | PL2_CLAMP_BIT;
                
                WriteMsr(MSR_PKG_POWER_LIMIT_ADDR, newValue);
                
                // Verify the write took effect
                ulong verify = ReadMsr(MSR_PKG_POWER_LIMIT_ADDR);
                double verifyPl1 = (verify & PL1_POWER_MASK) / 8.0;
                double verifyPl2 = ((verify >> 32) & 0x7FFF) / 8.0;
                
                bool pl1Ok = Math.Abs(verifyPl1 - pl1Watts) < 0.5;
                bool pl2Ok = Math.Abs(verifyPl2 - pl2Watts) < 0.5;
                
                // Also accept PL2=0 as "disabled, fine" even if we wanted PL2>0,
                // because the 32-bit MSR write path can't set PL2.
                // If high 32 bits are 0, then the PL2_ENABLE bit (bit 47) is also 0.
                if (!pl2Ok && verifyPl2 == 0 && (verify >> 32) == 0)
                {
                    Logger.WriteLine("[MSR] PL2 reads back as 0 (32-bit MSR write limitation). "
                                     + "PL2 is disabled — CPU will use PL1 as the only limit. Acceptable.");
                    pl2Ok = true;
                }
                
                if (pl1Ok && pl2Ok)
                {
                    Logger.WriteLine($"[MSR] Power limits set OK: PL1={verifyPl1:F1}W, PL2={verifyPl2:F1}W");
                    return true;
                }
                else
                {
                    Logger.WriteLine($"[MSR] Power limit verify failed: wanted PL1={pl1Watts}W/PL2={pl2Watts}W, got PL1={verifyPl1}W/PL2={verifyPl2}W (MSR locked by BIOS?)");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[MSR] Failed to set power limits: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Read the CPU's maximum power capability from MSR 0x614 (MSR_PKG_POWER_INFO).
        /// Bits 46:32 = Maximum Power in 1/8 Watt units.
        /// Falls back to TDP (bits 14:0) if max power is 0.
        /// </summary>
        public int ReadMaxPowerWatts()
        {
            if (!IsAvailable) return 150; // safe fallback
            if (CpuInfo.IsAMD) return 150; // AMD does not use MSR 0x614 for max power info, fallback to 150W for now

            
            try
            {
                ulong info = ReadMsr(0x614); // MSR_PKG_POWER_INFO
                uint maxPower = (uint)((info >> 32) & 0x7FFF); // bits 46:32
                if (maxPower > 0)
                    return (int)Math.Ceiling(maxPower / 8.0);
                    
                // Fallback to TDP (bits 14:0)
                uint tdp = (uint)(info & 0x7FFF);
                if (tdp > 0)
                    return (int)Math.Ceiling(tdp / 8.0) * 2; // 2x TDP as reasonable max
                    
                return 150; // safe fallback
            }
            catch
            {
                return 150;
            }
        }

        /// <summary>
        /// Read current package power limit (PL1) in watts.
        /// </summary>
        public double ReadPackagePowerLimit()
        {
            if (!IsAvailable) return 0;
            
            try
            {
                // MSR 0x610: MSR_PKG_POWER_LIMIT
                // Bits 14:0: Power Limit #1 in 1/8 Watt units
                ulong limit = ReadMsr(0x610);
                uint pl1 = (uint)(limit & 0x7FFF); // Bits 14:0
                return pl1 / 8.0; // Convert to watts
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Set package power limit (PL1) in watts.
        /// </summary>
        /// <param name="watts">Power limit in watts</param>
        public void SetPackagePowerLimit(double watts)
        {
            if (!IsAvailable) return;
            
            try
            {
                // MSR 0x610: MSR_PKG_POWER_LIMIT
                // Read current value to preserve other settings
                ulong current = ReadMsr(0x610);
                
                // Convert watts to 1/8 watt units
                uint pl1 = (uint)(watts * 8);
                pl1 = Math.Min(pl1, 0x7FFF); // Max 14 bits
                
                // Clear bits 14:0 and set new limit
                ulong newValue = (current & ~0x7FFFUL) | pl1;
                
                WriteMsr(0x610, newValue);
            }
            catch
            {
                // Silent fail
            }
        }
        
        // ==========================================
        // Intel RAPL — CPU Package Power Measurement
        // ==========================================
        
        private const uint MSR_RAPL_POWER_UNIT = 0x606;
        private const uint MSR_PKG_ENERGY_STATUS = 0x611;
        
        private double _raplEnergyUnit = 0;   // Joules per unit
        private uint _lastEnergyReading = 0;
        private DateTime _lastEnergyTimestamp = DateTime.MinValue;
        private bool _raplInitialized = false;
        
        /// <summary>
        /// Read CPU package power draw in watts via Intel RAPL MSR.
        /// Uses energy delta between successive calls to compute average power.
        /// Returns 0 on first call (needs two readings for delta).
        /// </summary>
        public double ReadCpuPackagePowerWatts()
        {
            if (!IsAvailable) return 0;
            
            try
            {
                // Initialize RAPL energy unit on first call
                if (!_raplInitialized)
                {
                    ulong powerUnit = ReadMsr(MSR_RAPL_POWER_UNIT);
                    // Bits [12:8] = Energy Status Units (ESU)
                    int esu = (int)((powerUnit >> 8) & 0x1F);
                    _raplEnergyUnit = 1.0 / (1 << esu); // Joules per unit
                    _raplInitialized = true;
                }
                
                // Read current energy counter (32-bit, wraps around)
                ulong energyStatus = ReadMsr(MSR_PKG_ENERGY_STATUS);
                uint currentEnergy = (uint)(energyStatus & 0xFFFFFFFF);
                DateTime now = DateTime.UtcNow;
                
                if (_lastEnergyTimestamp == DateTime.MinValue)
                {
                    // First reading — store and return 0
                    _lastEnergyReading = currentEnergy;
                    _lastEnergyTimestamp = now;
                    return 0;
                }
                
                double elapsed = (now - _lastEnergyTimestamp).TotalSeconds;
                if (elapsed < 0.1) return 0; // Too soon for meaningful delta
                
                // Handle 32-bit counter overflow
                uint delta;
                if (currentEnergy >= _lastEnergyReading)
                    delta = currentEnergy - _lastEnergyReading;
                else
                    delta = (uint.MaxValue - _lastEnergyReading) + currentEnergy + 1;
                
                double energyJoules = delta * _raplEnergyUnit;
                double watts = energyJoules / elapsed;
                
                // Update for next call
                _lastEnergyReading = currentEnergy;
                _lastEnergyTimestamp = now;
                
                // Sanity check: i9-13900HX max PL2 is ~157W package
                return watts > 500 ? 0 : Math.Round(watts, 1);
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Read current package power limit time window in seconds.
        /// </summary>
        public double ReadPackagePowerTimeWindow()
        {
            if (!IsAvailable) return 0;
            
            try
            {
                // MSR 0x610: MSR_PKG_POWER_LIMIT
                // Bits 23:17: Time Window for Power Limit #1 in 2^Y seconds
                ulong limit = ReadMsr(0x610);
                uint timeWindow = (uint)((limit >> 17) & 0x7F); // Bits 23:17
                return Math.Pow(2, timeWindow); // Convert to seconds
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Set package power limit time window in seconds.
        /// </summary>
        /// <param name="seconds">Time window in seconds</param>
        public void SetPackagePowerTimeWindow(double seconds)
        {
            if (!IsAvailable) return;
            
            try
            {
                // MSR 0x610: MSR_PKG_POWER_LIMIT
                // Read current value to preserve other settings
                ulong current = ReadMsr(0x610);
                
                // Convert seconds to 2^Y format
                int exponent = (int)Math.Round(Math.Log(seconds) / Math.Log(2));
                exponent = Math.Clamp(exponent, 0, 0x7F); // 7 bits
                
                // Clear bits 23:17 and set new time window
                ulong newValue = (current & ~(0x7FUL << 17)) | ((ulong)exponent << 17);
                
                WriteMsr(0x610, newValue);
            }
            catch
            {
                // Silent fail
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_handle != IntPtr.Zero && _pawnioClose != null)
            {
                _pawnioClose(_handle);
                _handle = IntPtr.Zero;
            }

            if (_pawnIOLib != IntPtr.Zero)
            {
                NativeMethods.FreeLibrary(_pawnIOLib);
                _pawnIOLib = IntPtr.Zero;
            }

            _moduleLoaded = false;
        }

        /// <summary>
        /// Check if PawnIO is installed on the system (static method for pre-instantiation checks).
        /// Delegates to centralized driver initialization helper.
        /// </summary>
        public static bool IsPawnIOInstalled() => DriverInitializationHelper.IsPawnIOInstalled();

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool FreeLibrary(IntPtr hModule);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        }
    }
}
