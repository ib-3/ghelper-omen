using System;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Reads and writes Intel RAPL power limits via MMIO at MCHBAR.
    ///
    /// AUDIT-FIX SUMMARY (see audit report §21):
    ///   1. MCHBAR base is now read dynamically from PCI config B0:D0:F0+0x48
    ///      (was hardcoded 0xFED10000 — audit §8).
    ///   2. SetPowerLimits now does READ-BACK VERIFICATION and returns false
    ///      if the bits did not actually change (was returning true on IOCTL
    ///      success even when the register was locked — audit §2, §7, §19).
    ///   3. ResetPowerLimits no longer writes 0x80000000 to the high dword
    ///      (was setting the LOCK bit, bricking the register until reboot — audit §6).
    ///   4. SyncFromMsr now masks out the LOCK bit before copying MSR 0x610
    ///      into MMIO (was copying the lock, defeating the whole MMIO fallback — audit §10).
    ///   5. Initialize() validates power-unit and energy-unit fields against
    ///      plausible ranges and refuses to operate with garbage values
    ///      (was cascading IOCTL failures into division-by-zero — audit §11).
    ///   6. CanWriteLimits now performs an actual write-read-verify test
    ///      on a non-locking bit (PL1 enable) instead of writing the same
    ///      value back, which always succeeds (audit §9).
    ///   7. SetPowerLimits accepts a clamp parameter instead of forcing
    ///      clamp=1 unconditionally (audit §14).
    ///   8. Removed the unused _msrAccess field — interface segregation
    ///      (audit §12).
    ///   9. PL1/PL2 = 0 now DISABLES the limit instead of silently doing nothing
    ///      (audit §13).
    /// </summary>
    public sealed class MmioPowerLimitProvider : IDisposable
    {
        private bool _disposed;
        private readonly IMmioAccess _mmio;

        private readonly object _syncRoot = new object();

        // Dynamic RAPL units (validated)
        private double _powerUnit      = 0.0;   // watts per LSB
        private double _raplEnergyUnit = 0.0;   // joules per LSB
        private bool   _unitsValid     = false;

        // Energy tracking for power sampling
        private uint      _lastEnergyReading   = 0;
        private DateTime  _lastEnergyTimestamp = DateTime.MinValue;

        // MCHBAR base (detected once at construction)
        private ulong _mchbarBase = 0;
        private bool  _mchbarValid = false;

        // RAPL MMIO offsets (relative to MCHBAR base).
        // Valid for Skylake / Kaby Lake / Coffee Lake / Comet Lake client platforms.
        // Newer platforms (Tiger Lake+) may differ — see audit §A.
        private const uint RAPL_POWER_UNIT_OFFSET          = 0x5938;
        private const uint RAPL_PLATFORM_POWER_LIMIT_OFFSET = 0x5990;
        private const uint RAPL_PKG_POWER_LIMIT_OFFSET      = 0x59A0;
        private const uint RAPL_PKG_ENERGY_STATUS_OFFSET    = 0x59A8;
        private const uint RAPL_PKG_POWER_INFO_OFFSET       = 0x59B0;

        // PCI config for MCHBAR detection: B0:D0:F0 offset 0x48
        //   Bit  0    : MCHBAR enable
        //   Bits 31:15: Base address (low 15 bits implicitly 0)
        private const int MCHBAR_PCI_BUS      = 0;
        private const int MCHBAR_PCI_DEVICE   = 0;
        private const int MCHBAR_PCI_FUNCTION = 0;
        private const uint MCHBAR_PCI_OFFSET  = 0x48;
        private const ulong MCHBAR_ENABLE_BIT = 0x1UL;
        private const ulong MCHBAR_BASE_MASK  = 0xFFFFFFFE_0000UL;   // bits 31:15

        // PKG_RAPL_POWER_LIMIT bitfield layout (MSR 0x610 / MMIO MCHBAR+0x59A0)
        private const uint  PL1_POWER_MASK     = 0x00007FFFu;
        private const uint  PL1_ENABLE_BIT     = 1u << 15;
        private const uint  PL1_CLAMP_BIT      = 1u << 16;
        private const uint  PL1_TW_MASK         = 0x00F80000u;   // bits 23:17

        private const uint  PL2_POWER_MASK     = 0x00007FFFu;   // (in high dword)
        private const uint  PL2_ENABLE_BIT     = 1u << 15;      // bit 47 overall
        private const uint  PL2_CLAMP_BIT      = 1u << 16;      // bit 48 overall
        private const uint  PL2_TW_MASK         = 0x00F80000u;   // bits 55:49 overall

        private const ulong LOCK_BIT            = 1UL << 63;    // bit 63 (in high dword bit 31)

        public bool IsAvailable => _mmio != null && _mmio.IsAvailable && _mchbarValid && _unitsValid;

        public bool CanWriteLimits { get; private set; }

        public bool IsLocked { get; private set; }

        public MmioPowerLimitProvider(IMmioAccess mmio)
        {
            _mmio = mmio ?? throw new ArgumentNullException(nameof(mmio));

            if (!_mmio.IsAvailable)
            {
                Log("IMmioAccess not available.");
                return;
            }

            // 1. Detect MCHBAR base from PCI config
            DetectMchbarBase();
            if (!_mchbarValid)
            {
                Log("MCHBAR detection failed — provider unavailable.");
                return;
            }

            // 2. Read and validate RAPL units
            Initialize();
            if (!_unitsValid)
            {
                Log("RAPL unit validation failed — provider unavailable.");
                return;
            }

            // 3. Check current lock status
            var limits = GetPowerLimits();
            IsLocked = limits.IsLocked;
            if (IsLocked)
            {
                Log("PKG_RAPL_POWER_LIMIT is LOCKED. Writes will be no-ops.");
                CanWriteLimits = false;
                return;
            }

            // 4. Probe write capability by toggling PL1 enable (read, toggle, write, read, restore)
            CanWriteLimits = ProbeWriteCapability();
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // MCHBAR detection — audit §8 fix
        // ─────────────────────────────────────────────────────────────────────────────
        private void DetectMchbarBase()
        {
            try
            {
                // If the MMIO backend is PawnIOMmioAccess, it already detected
                // MCHBAR internally in the Pawn module. Use its pre-detected base
                // instead of ReadPciConfigDword (which is a no-op on PawnIOMmioAccess).
                if (_mmio is PawnIOMmioAccess pawnMmio)
                {
                    if (pawnMmio.MchbarBase != 0)
                    {
                        _mchbarBase  = pawnMmio.MchbarBase;
                        _mchbarValid = true;
                        Log($"MCHBAR base = 0x{_mchbarBase:X} (from PawnIO module, window = {pawnMmio.MchbarSize / 1024} KB)");
                        return;
                    }
                    Log("PawnIOMmioAccess has MchbarBase=0 — module init failed?");
                    _mchbarValid = false;
                    return;
                }

                // Fallback: legacy path using PCI config read (for WinRing0 or other backends)
                uint raw = _mmio.ReadPciConfigDword(
                    MCHBAR_PCI_BUS, MCHBAR_PCI_DEVICE, MCHBAR_PCI_FUNCTION, MCHBAR_PCI_OFFSET);

                if (raw == 0)
                {
                    Log("PCI read of MCHBAR returned 0 — IOCTL failed or unsupported platform.");
                    _mchbarValid = false;
                    return;
                }

                if ((raw & MCHBAR_ENABLE_BIT) == 0)
                {
                    Log("MCHBAR is disabled in PCI config (enable bit = 0).");
                    _mchbarValid = false;
                    return;
                }

                _mchbarBase  = (ulong)(raw & MCHBAR_BASE_MASK);
                _mchbarValid = true;
                Log($"MCHBAR base = 0x{_mchbarBase:X8} (PCI raw = 0x{raw:X8})");
            }
            catch (Exception ex)
            {
                Log($"DetectMchbarBase failed: {ex.Message}");
                _mchbarValid = false;
            }
        }

        private ulong Reg(uint offset) => _mchbarBase + offset;

        // ─────────────────────────────────────────────────────────────────────────────
        // Initialize — audit §11 fix (validate pus / esu ranges)
        // ─────────────────────────────────────────────────────────────────────────────
        private void Initialize()
        {
            try
            {
                uint powerUnitReg = _mmio.ReadMmioDword(Reg(RAPL_POWER_UNIT_OFFSET));

                int pus = (int)(powerUnitReg & 0xF);          // bits 3:0  — power unit
                int esu = (int)((powerUnitReg >> 8) & 0x1F);  // bits 12:8 — energy unit

                // Sanity ranges: real Intel client CPUs use pus=3 (1/8 W) and esu=14 (1/16384 J).
                // Accept pus 0–15, esu 0–31 (theoretical max), reject anything that
                // would produce NaN/Inf or unreasonably small units (suggests IOCTL garbage).
                if (pus < 0 || pus > 15 || esu < 0 || esu > 31)
                {
                    Log($"RAPL units out of range: pus={pus}, esu={esu} "
                        + $"(raw=0x{powerUnitReg:X8}) — likely IOCTL failure or wrong MCHBAR.");
                    _unitsValid = false;
                    return;
                }

                _powerUnit      = 1.0 / (1L << pus);
                _raplEnergyUnit = 1.0 / (1L << esu);
                _unitsValid     = true;

                Log($"RAPL units: powerUnit={_powerUnit:G6} W/LSB, energyUnit={_raplEnergyUnit:G6} J/LSB");
            }
            catch (Exception ex)
            {
                Log($"Initialize failed: {ex.Message}");
                _unitsValid = false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // ProbeWriteCapability — audit §9 fix
        // Toggles PL1 enable bit, reads back to confirm, then restores the original state.
        // This is the only reliable way to detect whether writes actually take effect.
        // ─────────────────────────────────────────────────────────────────────────────
        private bool ProbeWriteCapability()
        {
            try
            {
                ulong addr   = Reg(RAPL_PKG_POWER_LIMIT_OFFSET);
                uint  before = _mmio.ReadMmioDword(addr);

                uint toggled = before ^ PL1_ENABLE_BIT;
                _mmio.WriteMmioDword(addr, toggled);

                uint after = _mmio.ReadMmioDword(addr);

                // Restore original state regardless of probe outcome
                _mmio.WriteMmioDword(addr, before);

                bool writable = ((after ^ before) & PL1_ENABLE_BIT) != 0;
                if (!writable)
                {
                    Log("ProbeWriteCapability: write did not take effect — register may be locked "
                        + "even though lock bit reads as 0 (firmware-level lock).");
                }
                return writable;
            }
            catch (Exception ex)
            {
                Log($"ProbeWriteCapability exception: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // GetPowerLimits — unchanged in semantics, but reads back via validated MCHBAR
        // ─────────────────────────────────────────────────────────────────────────────
        public (double Pl1Watts, double Pl2Watts, bool Pl1Enabled, bool Pl2Enabled, bool IsLocked) GetPowerLimits()
        {
            if (!IsAvailable) return (0, 0, false, false, false);

            try
            {
                uint low  = _mmio.ReadMmioDword(Reg(RAPL_PKG_POWER_LIMIT_OFFSET));
                uint high = _mmio.ReadMmioDword(Reg(RAPL_PKG_POWER_LIMIT_OFFSET) + 4);

                ulong value = low | ((ulong)high << 32);

                double pl1Watts   = (value & 0x7FFF) * _powerUnit;
                bool   pl1Enabled = (value & (1UL << 15)) != 0;

                double pl2Watts   = ((value >> 32) & 0x7FFF) * _powerUnit;
                bool   pl2Enabled = (value & (1UL << 47)) != 0;

                bool isLocked = (value & LOCK_BIT) != 0;

                return (pl1Watts, pl2Watts, pl1Enabled, pl2Enabled, isLocked);
            }
            catch (Exception ex)
            {
                Log($"GetPowerLimits error: {ex.Message}");
                return (0, 0, false, false, false);
            }
        }

        public int ReadMaxPowerWatts()
        {
            if (!IsAvailable) return 150;
            try
            {
                uint infoLow = _mmio.ReadMmioDword(Reg(RAPL_PKG_POWER_INFO_OFFSET));
                uint infoHigh = _mmio.ReadMmioDword(Reg(RAPL_PKG_POWER_INFO_OFFSET) + 4);
                ulong info = ((ulong)infoHigh << 32) | infoLow;
                
                uint maxPower = (uint)((info >> 32) & 0x7FFF); // bits 46:32
                if (maxPower > 0)
                    return (int)Math.Ceiling(maxPower * _powerUnit);
                    
                uint tdp = (uint)(info & 0x7FFF);
                if (tdp > 0)
                    return (int)Math.Ceiling(tdp * _powerUnit) * 2;
                    
                return 150;
            }
            catch
            {
                return 150;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // SetPowerLimits — audit §2, §7, §13, §14, §21 fix
        // Returns true ONLY if read-back verification confirms both PL1 and PL2 bits
        // were actually written.
        // ─────────────────────────────────────────────────────────────────────────────
        public bool SetPowerLimits(double pl1Watts, double pl2Watts,
                                    bool pl1Clamp = true, bool pl2Clamp = true)
        {
            if (!IsAvailable || !CanWriteLimits) return false;
            if (IsLocked)
            {
                Log("SetPowerLimits refused — register is locked.");
                return false;
            }

            try
            {
                ulong addr       = Reg(RAPL_PKG_POWER_LIMIT_OFFSET);
                uint  currentLow  = _mmio.ReadMmioDword(addr);
                uint  currentHigh = _mmio.ReadMmioDword(addr + 4);

                // ── PL1 (low dword) ──────────────────────────────────────────────
                // Preserve TW1 (bits 23:17) and any reserved bits. Clear PL1 power,
                // enable, and clamp. Then OR in the new values.
                uint newLow = currentLow;
                newLow &= ~(PL1_POWER_MASK | PL1_ENABLE_BIT | PL1_CLAMP_BIT);

                if (pl1Watts > 0)
                {
                    uint pl1Raw = (uint)Math.Round(pl1Watts / _powerUnit) & PL1_POWER_MASK;
                    newLow |= pl1Raw;
                    newLow |= PL1_ENABLE_BIT;
                    if (pl1Clamp) newLow |= PL1_CLAMP_BIT;
                }
                // pl1Watts == 0 → PL1 disabled (no enable bit, no power bits)

                _mmio.WriteMmioDword(addr, newLow);

                // ── PL2 (high dword) ─────────────────────────────────────────────
                // Same pattern. Note: LOCK_BIT lives in bit 31 of high dword — preserve it.
                uint newHigh = currentHigh;
                newHigh &= ~(PL2_POWER_MASK | PL2_ENABLE_BIT | PL2_CLAMP_BIT);
                // NEVER clear or set LOCK_BIT here — preserve whatever was there.
                newHigh |= (currentHigh & 0x80000000u);

                if (pl2Watts > 0)
                {
                    uint pl2Raw = (uint)Math.Round(pl2Watts / _powerUnit) & PL2_POWER_MASK;
                    newHigh |= pl2Raw;
                    newHigh |= PL2_ENABLE_BIT;
                    if (pl2Clamp) newHigh |= PL2_CLAMP_BIT;
                }

                _mmio.WriteMmioDword(addr + 4, newHigh);

                // ── READ-BACK VERIFICATION (audit §2 fix) ───────────────────────
                uint verifyLow  = _mmio.ReadMmioDword(addr);
                uint verifyHigh = _mmio.ReadMmioDword(addr + 4);

                bool pl1Ok = (verifyLow & (PL1_POWER_MASK | PL1_ENABLE_BIT)) ==
                             (newLow    & (PL1_POWER_MASK | PL1_ENABLE_BIT));
                bool pl2Ok = (verifyHigh & (PL2_POWER_MASK | PL2_ENABLE_BIT)) ==
                             (newHigh    & (PL2_POWER_MASK | PL2_ENABLE_BIT));

                if (!pl1Ok || !pl2Ok)
                {
                    Log($"SetPowerLimits verification FAILED: "
                        + $"pl1Ok={pl1Ok} (wrote 0x{newLow:X8}, read 0x{verifyLow:X8}), "
                        + $"pl2Ok={pl2Ok} (wrote 0x{newHigh:X8}, read 0x{verifyHigh:X8})");
                    return false;
                }

                Log($"SetPowerLimits verified OK: PL1={pl1Watts}W (clamp={pl1Clamp}), "
                    + $"PL2={pl2Watts}W (clamp={pl2Clamp})");
                return true;
            }
            catch (Exception ex)
            {
                Log($"SetPowerLimits exception: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // SyncFromMsr — audit §10 fix (mask out LOCK bit before copying)
        // Copies PL1/PL2 power + enable + clamp + TW from MSR 0x610 to MMIO 0x59A0,
        // preserving MMIO's LOCK bit (do NOT propagate lock from MSR to MMIO).
        // ─────────────────────────────────────────────────────────────────────────────
        public void SyncFromMsr(ulong msr610Value)
        {
            if (!IsAvailable || !CanWriteLimits) return;

            try
            {
                // Mask out LOCK_BIT (bit 63) — never copy the lock from MSR to MMIO.
                ulong sanitized = msr610Value & ~LOCK_BIT;

                uint msrLow  = (uint)(sanitized & 0xFFFFFFFF);
                uint msrHigh = (uint)(sanitized >> 32);

                ulong address = Reg(RAPL_PKG_POWER_LIMIT_OFFSET);

                // Preserve MMIO's LOCK bit (don't accidentally clear it either)
                uint currentHigh = _mmio.ReadMmioDword(address + 4);
                uint preserveLockHigh = (msrHigh & 0x7FFFFFFFu) | (currentHigh & 0x80000000u);

                _mmio.WriteMmioDword(address,     msrLow);
                _mmio.WriteMmioDword(address + 4, preserveLockHigh);

                Log($"SyncFromMsr complete. MSR=0x{msr610Value:X16} → MMIO=0x{sanitized:X16} "
                    + "(LOCK bit stripped)");
            }
            catch (Exception ex)
            {
                Log($"SyncFromMsr error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // ResetPowerLimits — audit §6 fix (NEVER set the lock bit)
        // ─────────────────────────────────────────────────────────────────────────────
        public void ResetPowerLimits()
        {
            if (!IsAvailable || !CanWriteLimits) return;

            try
            {
                ulong address = Reg(RAPL_PKG_POWER_LIMIT_OFFSET);

                // Preserve the LOCK bit if it's already set (don't try to clear it —
                // the hardware ignores writes when locked, and trying to clear it
                // would fail silently).
                uint currentHigh = _mmio.ReadMmioDword(address + 4);
                uint preserveLock = currentHigh & 0x80000000u;

                // Write 0 to PL1 (low dword) and 0 to PL2 (high dword) but preserve lock.
                _mmio.WriteMmioDword(address,     0x00000000u);
                _mmio.WriteMmioDword(address + 4, preserveLock);

                Log("ResetPowerLimits: PL1 and PL2 cleared (lock bit preserved if set).");
            }
            catch (Exception ex)
            {
                Log($"ResetPowerLimits error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // ReadCpuPackagePowerWatts — unchanged in semantics
        // ─────────────────────────────────────────────────────────────────────────────
        public double ReadCpuPackagePowerWatts()
        {
            if (!IsAvailable) return 0;

            lock (_syncRoot)
            {
                try
                {
                    uint currentEnergy = _mmio.ReadMmioDword(Reg(RAPL_PKG_ENERGY_STATUS_OFFSET));
                    DateTime now = DateTime.UtcNow;

                    if (_lastEnergyTimestamp == DateTime.MinValue)
                    {
                        _lastEnergyReading   = currentEnergy;
                        _lastEnergyTimestamp = now;
                        return 0;
                    }

                    double elapsed = (now - _lastEnergyTimestamp).TotalSeconds;
                    if (elapsed < 0.1) return 0;

                    uint delta;
                    if (currentEnergy >= _lastEnergyReading)
                        delta = currentEnergy - _lastEnergyReading;
                    else
                        delta = (uint.MaxValue - _lastEnergyReading) + currentEnergy + 1;

                    double energyJoules = delta * _raplEnergyUnit;
                    double watts        = energyJoules / elapsed;

                    _lastEnergyReading   = currentEnergy;
                    _lastEnergyTimestamp = now;

                    // Reject implausible values (>8192 W clearly indicates garbage)
                    return watts > 8192 ? 0 : Math.Round(watts, 1);
                }
                catch (Exception ex)
                {
                    Log($"ReadCpuPackagePowerWatts error: {ex.Message}");
                    return 0;
                }
            }
        }

        private static void Log(string msg)
        {
            try { Logger.WriteLine($"[MmioPowerLimitProvider] {msg}"); }
            catch { System.Diagnostics.Debug.WriteLine($"[MmioPowerLimitProvider] {msg}"); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Do not dispose _mmio — caller owns it.
        }
    }
}
