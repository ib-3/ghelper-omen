using System;
using OmenCore.Hardware;

namespace GHelper.Omen.Lighting
{
    public class WmiLightingBackend : IOmenLightingBackend
    {
        private readonly IHpWmiBios _bios;

        public string Name => "WMI (4-Zone)";
        public OmenRgbMethod Method => OmenRgbMethod.Wmi;
        
        // WMI is available if the driver is loaded and returns valid data
        public bool IsAvailable => _bios.IsAvailable;
        public bool IsPerKey => false;
        public int ZoneCount => 4;

        public WmiLightingBackend(IHpWmiBios bios)
        {
            _bios = bios;
        }

        public bool SetColorTable(byte[] zoneColors)
        {
            if (zoneColors == null || zoneColors.Length < 12)
                return false;

            return _bios.SetColorTable(zoneColors);
        }

        public bool SetPerKeyColor(int keyId, byte r, byte g, byte b)
        {
            // WMI doesn't support per-key standardly, ignore
            return false;
        }
    }
}
