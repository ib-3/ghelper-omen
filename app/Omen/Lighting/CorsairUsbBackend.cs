using System;
using System.Linq;
using System.Drawing;
using System.Threading;
using HidSharp;
using GHelper.Omen;

namespace GHelper.Omen.Lighting
{
    public class CorsairUsbBackend : IOmenLightingBackend
    {
        private HidDevice? _device;

        private const int CORSAIR_VID = 0x1B1C;

        public string Name => "Corsair USB (Per-Key)";
        public OmenRgbMethod Method => OmenRgbMethod.CorsairUsb;
        
        public bool IsAvailable 
        {
            get 
            {
                if (_device == null)
                {
                    _device = FindCorsairDevice();
                }
                return _device != null;
            }
        }
        
        public bool IsPerKey => true;
        public int ZoneCount => 1;

        public CorsairUsbBackend()
        {
        }

        private HidDevice? FindCorsairDevice()
        {
            try
            {
                var devices = DeviceList.Local.GetHidDevices(CORSAIR_VID);
                foreach (var dev in devices)
                {
                    if (dev.GetMaxOutputReportLength() >= 64)
                    {
                        return dev;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"CorsairUsbBackend: Failed to find device: {ex.Message}");
            }
            return null;
        }

        public bool SetColorTable(byte[] zoneColors)
        {
            if (zoneColors == null || zoneColors.Length < 3) return false;
            return SetPerKeyColor(0xFFFF, zoneColors[0], zoneColors[1], zoneColors[2]);
        }

        public bool SetPerKeyColor(int keyId, byte r, byte g, byte b)
        {
            var dev = FindCorsairDevice();
            if (dev == null) return false;

            try
            {
                using (var stream = dev.Open())
                {
                    byte[] report = new byte[65];
                    report[0] = 0x00; // Report ID
                    report[1] = 0x07; // Corsair Command
                    report[2] = 0x22; 
                    
                    // Hardware lighting mode assignment
                    report[3] = 0x01; // Static
                    report[4] = r;
                    report[5] = g;
                    report[6] = b;
                    
                    stream.Write(report, 0, report.Length);
                    Logger.WriteLine($"CorsairUsbBackend: Set color to R={r} G={g} B={b}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"CorsairUsbBackend.SetPerKeyColor: Error: {ex.Message}");
                return false;
            }
        }
    }
}
