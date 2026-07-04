namespace OmenCore.Hardware
{
    /// <summary>
    /// Abstraction over a ring-0 driver that can read/write MMIO and PCI config space.
    ///
    /// AUDIT-FIX (see audit §22): added ReadPciConfigDword so MmioPowerLimitProvider
    /// can detect MCHBAR dynamically from PCI B0:D0:F0+0x48 instead of hardcoding
    /// 0xFED10000.
    /// </summary>
    public interface IMmioAccess
    {
        bool IsAvailable { get; }

        /// <summary>
        /// Read a 32-bit MMIO register at the given physical address.
        /// </summary>
        uint ReadMmioDword(ulong address);

        /// <summary>
        /// Write a 32-bit value to an MMIO register at the given physical address.
        /// </summary>
        void WriteMmioDword(ulong address, uint value);

        /// <summary>
        /// Read a 32-bit PCI configuration register.
        /// Used for dynamic MCHBAR detection (B0:D0:F0 offset 0x48).
        /// </summary>
        uint ReadPciConfigDword(int bus, int device, int function, uint regOffset);
    }
}
