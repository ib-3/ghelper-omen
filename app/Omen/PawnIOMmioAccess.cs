using System;
using System.IO;

namespace OmenCore.Hardware
{
    /// <summary>
    /// PawnIO-based MMIO access via the IntelMCHBAR module.
    /// Secure-Boot compatible — no unsigned WinRing0 driver required.
    ///
    /// REQUIRES the patched IntelMCHBAR.bin (with ioctl_write_dword added)
    /// to be loaded on the same PawnIO handle as IntelMSR.bin.
    /// See /home/z/my-project/download/pawnio/IntelMCHBAR.p for the
    /// patched Pawn source.
    ///
    /// The Pawn module handles:
    ///   - CPU family/model detection (Sandy Bridge → Novalake)
    ///   - MCHBAR base detection from PCI config B0:D0:F0+0x48
    ///   - MCHBAR enable-bit validation
    ///   - MCHBAR window sizing (32/64/128 KB based on CPU gen)
    ///   - io_space_map / io_space_unmap lifecycle
    ///   - Offset bounds checking + alignment validation
    ///   - Optional write-offset whitelist (RAPL power-limit offsets only)
    ///
    /// CELL SIZE NOTE:
    ///   The PawnIO user-mode API uses 8-byte (ulong) cells, but the Pawn
    ///   VM uses 4-byte cells internally. Only the low 32 bits of each
    ///   ulong carry a Pawn value. This matches the existing convention
    ///   in PawnIOMsrAccess (see how ReadMsr returns output[0] | output[1]<<32).
    ///   All offsets and values passed to the MCHBAR module fit in 32 bits
    ///   (max MCHBAR window is 128 KB), so no splitting is needed.
    /// </summary>
    public sealed class PawnIOMmioAccess : IMmioAccess, IDisposable
    {
        private readonly PawnIOMsrAccess _pawnio;
        private readonly bool _moduleLoaded;
        private readonly ulong _mchbarBase;
        private readonly uint _mchbarSize;
        private bool _disposed;

        // PawnIO ioctl names exported by the patched IntelMCHBAR module
        private const string IOCTL_READ_DWORD     = "ioctl_read_dword";
        private const string IOCTL_READ_QWORD     = "ioctl_read_qword";
        private const string IOCTL_WRITE_DWORD    = "ioctl_write_dword";
        private const string IOCTL_GET_MCHBAR_ADDR = "ioctl_get_mchbar_addr";
        private const string IOCTL_GET_MCHBAR_SIZE = "ioctl_get_mchbar_size";

        public PawnIOMmioAccess(PawnIOMsrAccess pawnio)
        {
            _pawnio = pawnio ?? throw new ArgumentNullException(nameof(pawnio));

            if (!_pawnio.IsAvailable)
            {
                Logger.WriteLine("[PawnIOMmioAccess] PawnIO not available.");
                return;
            }

            if (!LoadMchbarModule())
            {
                Logger.WriteLine("[PawnIOMmioAccess] Failed to load IntelMCHBAR module. "
                                 + "Verify drivers/IntelMCHBAR.bin exists and is signed.");
                return;
            }

            // Read back the detected MCHBAR base — proves the module initialized
            // successfully (main() returned STATUS_SUCCESS) and gives us a value
            // to log for diagnostics.
            // ioctl_get_mchbar_addr: DEFINE_IOCTL_SIZED(0, 1) → in_size=0, out_size=1
            if (!TryExecute(IOCTL_GET_MCHBAR_ADDR, Array.Empty<ulong>(), 1, out ulong[] addrOut) ||
                addrOut.Length < 1)
            {
                Logger.WriteLine("[PawnIOMmioAccess] ioctl_get_mchbar_addr failed — "
                                 + "module loaded but MCHBAR detection failed. "
                                 + "Likely non-Intel CPU, MCHBAR disabled in BIOS, or "
                                 + "unsupported CPU family.");
                _moduleLoaded = false;
                return;
            }

            _mchbarBase = addrOut[0] & 0xFFFFFFFF_FFFFFFFFUL;  // low 32 bits typically

            // Read the window size (added in the patched module)
            // ioctl_get_mchbar_size: DEFINE_IOCTL_SIZED(0, 1) → in_size=0, out_size=1
            if (TryExecute(IOCTL_GET_MCHBAR_SIZE, Array.Empty<ulong>(), 1, out ulong[] sizeOut) &&
                sizeOut.Length >= 1)
            {
                _mchbarSize = (uint)(sizeOut[0] & 0xFFFFFFFFUL);
            }
            else
            {
                // Default to 32 KB if the ioctl is missing (older module)
                _mchbarSize = 0x8000;
            }

            _moduleLoaded = true;
            Logger.WriteLine($"[PawnIOMmioAccess] IntelMCHBAR loaded. "
                             + $"MCHBAR base = 0x{_mchbarBase:X}, window = {_mchbarSize / 1024} KB.");
        }

        public bool IsAvailable => _pawnio.IsAvailable && _moduleLoaded;

        /// <summary>Detected MCHBAR physical base address (read from PCI config by the Pawn module).</summary>
        public ulong MchbarBase => _mchbarBase;

        /// <summary>MCHBAR window size in bytes (32/64/128 KB depending on CPU gen).</summary>
        public uint MchbarSize => _mchbarSize;

        private bool LoadMchbarModule()
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] names = { "IntelMCHBAR.bin", "IntelMCHBAR.amx" };

                foreach (var n in names)
                {
                    string path = Path.Combine(appDir, "drivers", n);
                    if (File.Exists(path))
                    {
                        byte[] blob = File.ReadAllBytes(path);
                        if (_pawnio.LoadAdditionalModule(blob))
                        {
                            Logger.WriteLine($"[PawnIOMmioAccess] Loaded module: {path}");
                            return true;
                        }
                        Logger.WriteLine($"[PawnIOMmioAccess] pawnio_load rejected {n} — "
                                         + "module may be unsigned or for wrong PawnIO version.");
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[PawnIOMmioAccess] LoadMchbarModule exception: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // IMmioAccess implementation
        //
        // The C# caller (MmioPowerLimitProvider) builds absolute physical
        // addresses as (MchbarBase + offset). The Pawn module takes offsets
        // relative to the MCHBAR base, so we subtract the base here.
        // ─────────────────────────────────────────────────────────────────

        public uint ReadMmioDword(ulong address)
        {
            if (!IsAvailable) return 0;

            // Convert absolute physical address to MCHBAR offset
            if (address < _mchbarBase)
            {
                Logger.WriteLine($"[PawnIOMmioAccess] ReadMmioDword: address 0x{address:X} "
                                 + $"is below MCHBAR base 0x{_mchbarBase:X}.");
                return 0;
            }
            uint offset = (uint)(address - _mchbarBase);
            return ReadDwordAtOffset(offset);
        }

        public void WriteMmioDword(ulong address, uint value)
        {
            if (!IsAvailable) return;

            if (address < _mchbarBase)
            {
                Logger.WriteLine($"[PawnIOMmioAccess] WriteMmioDword: address 0x{address:X} "
                                 + $"is below MCHBAR base 0x{_mchbarBase:X}.");
                return;
            }
            uint offset = (uint)(address - _mchbarBase);
            WriteDwordAtOffset(offset, value);
        }

        /// <summary>
        /// IMmioAccess.ReadPciConfigDword — NOT NEEDED with the Pawn module,
        /// because MCHBAR detection is internal to the Pawn module. The
        /// interface method is implemented as a no-op for compatibility;
        /// callers that need MCHBAR should read the MchbarBase property.
        /// </summary>
        public uint ReadPciConfigDword(int bus, int device, int function, uint regOffset)
        {
            Logger.WriteLine("[PawnIOMmioAccess] ReadPciConfigDword not supported — "
                             + "MCHBAR detection is internal to the Pawn module. "
                             + "Use MchbarBase property instead.");
            return 0;
        }

        // ─────────────────────────────────────────────────────────────────
        // Direct offset-based API — preferred when the caller already knows
        // the MCHBAR offset (e.g. RAPL_POWER_LIMIT = 0x59A0). Avoids the
        // absolute-address dance and is clearer in traces.
        // ─────────────────────────────────────────────────────────────────

        public uint ReadDwordAtOffset(uint offset)
        {
            if (!IsAvailable) return 0;

            // ioctl_read_dword: DEFINE_IOCTL_SIZED(1, 1) → in_size=1, out_size=1
            if (!TryExecute(IOCTL_READ_DWORD, new ulong[] { offset }, 1, out ulong[] result) ||
                result.Length < 1)
            {
                Logger.WriteLine($"[PawnIOMmioAccess] ioctl_read_dword failed @ +0x{offset:X}");
                return 0;
            }
            return (uint)(result[0] & 0xFFFFFFFFUL);
        }

        public bool WriteDwordAtOffset(uint offset, uint value)
        {
            if (!IsAvailable) return false;

            // ioctl_write_dword: DEFINE_IOCTL_SIZED(2, 0) → in_size=2, out_size=0
            if (!TryExecute(IOCTL_WRITE_DWORD, new ulong[] { offset, value }, 0, out _))
            {
                Logger.WriteLine($"[PawnIOMmioAccess] ioctl_write_dword failed @ +0x{offset:X} "
                                 + $"(rejected by whitelist? offset out of range? not aligned?)");
                return false;
            }
            return true;
        }

        public ulong ReadQwordAtOffset(uint offset)
        {
            if (!IsAvailable) return 0;

            // ioctl_read_qword: DEFINE_IOCTL_SIZED(1, 1) → in_size=1, out_size=1
            if (!TryExecute(IOCTL_READ_QWORD, new ulong[] { offset }, 1, out ulong[] result) ||
                result.Length < 1)
            {
                Logger.WriteLine($"[PawnIOMmioAccess] ioctl_read_qword failed @ +0x{offset:X}");
                return 0;
            }
            // Note: the Pawn module's out[0] returns a 32-bit cell value.
            // For a 64-bit qword read, you'd need to call this twice (low, high)
            // OR extend the Pawn module to return two cells. The current
            // IntelMCHBAR.p virtual_read_qword native returns a single 64-bit
            // value, which the PawnIO runtime packs into one cell if it fits.
            // Verify behavior on the target platform.
            return result[0];
        }

        // ─────────────────────────────────────────────────────────────────
        // PawnIO execution helper — wraps _pawnio.Execute with logging
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Execute a PawnIO ioctl with exact input/output sizes matching the Pawn module's
        /// DEFINE_IOCTL_SIZED declaration. PawnIO validates sizes exactly — passing more
        /// elements than expected causes E_INVALIDARG (0x80070057).
        /// </summary>
        /// <param name="command">Ioctl name (e.g. "ioctl_read_dword")</param>
        /// <param name="input">Input array — Length must match the Pawn ioctl's in_size</param>
        /// <param name="expectedOutputSize">Must match the Pawn ioctl's out_size exactly</param>
        /// <param name="output">Receives the output array (sized to expectedOutputSize)</param>
        private bool TryExecute(string command, ulong[] input, int expectedOutputSize, out ulong[] output)
        {
            output = expectedOutputSize > 0 ? new ulong[expectedOutputSize] : Array.Empty<ulong>();
            int hr;
            try
            {
                hr = _pawnio.Execute(command, input, output);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"[PawnIOMmioAccess] Execute '{command}' threw: {ex.Message}");
                return false;
            }
            if (hr < 0)
            {
                Logger.WriteLine($"[PawnIOMmioAccess] '{command}' returned HRESULT 0x{hr:X8}");
                return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Don't dispose _pawnio — caller owns it (shared handle).
        }
    }
}
