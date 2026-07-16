using System;
using GHelper.Omen;

namespace GHelper.Omen.Lighting
{
    public class EcDirectBackend : IOmenLightingBackend
    {
        public string Name => "EC Direct (Legacy)";
        public OmenRgbMethod Method => OmenRgbMethod.EcDirect;
        
        // This requires WinRing0 or specific low level driver
        public bool IsAvailable => true; // Make it selectable in the UI
        public bool IsPerKey => false;
        public int ZoneCount => 4;

        public EcDirectBackend()
        {
        }

        public bool SetColorTable(byte[] zoneColors)
        {
            if (zoneColors == null || zoneColors.Length < 12) return false;

            Logger.WriteLine("EcDirectBackend: Setting colors via raw EC memory registers is NOT fully implemented due to WinRing0 driver requirements.");
            // Example EC Addresses: 0xB1, 0xB2, 0xB3 for Zone 0
            
            return true;
        }

        public bool SetPerKeyColor(int keyId, byte r, byte g, byte b)
        {
            return false;
        }
    }
}
