using System;
using System.Linq;
using System.Drawing;
using System.Threading;
using HidSharp;
using GHelper.Omen;

namespace GHelper.Omen.Lighting
{
    public class LogitechUsbBackend : IOmenLightingBackend
    {
        private HidDevice? _device;

        private const int LOGITECH_VID = 0x046D;

        public string Name => "Logitech USB (Per-Key)";
        public OmenRgbMethod Method => OmenRgbMethod.LogitechUsb;
        
        public bool IsAvailable 
        {
            get 
            {
                if (_device == null)
                {
                    _device = FindLogitechGDevice();
                }
                return _device != null;
            }
        }
        
        public bool IsPerKey => true;
        public int ZoneCount => 1; // It's per-key, but we can treat as 1 zone if we set all keys

        public LogitechUsbBackend()
        {
        }

        private HidDevice? FindLogitechGDevice()
        {
            try
            {
                var devices = DeviceList.Local.GetHidDevices(LOGITECH_VID);
                foreach (var dev in devices)
                {
                    // Basic heuristic: Logitech G series keyboards often start with 0xC3xx
                    if (dev.ProductID >= 0xC300 && dev.ProductID <= 0xC3FF)
                    {
                        // Check for RGB interface
                        if (dev.GetMaxOutputReportLength() >= 20)
                        {
                            return dev;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechUsbBackend: Failed to find device: {ex.Message}");
            }
            return null;
        }

        public bool SetColorTable(byte[] zoneColors)
        {
            if (zoneColors == null || zoneColors.Length < 3) return false;
            // Map the first zone's color to the entire keyboard
            return SetPerKeyColor(0xFFFF, zoneColors[0], zoneColors[1], zoneColors[2]);
        }

        public bool SetPerKeyColor(int keyId, byte r, byte g, byte b)
        {
            var dev = FindLogitechGDevice();
            if (dev == null) return false;

            try
            {
                using (var stream = dev.Open())
                {
                    // Logitech generic RGB report (Feature 0x8070 or 0x8081 usually, but simplified direct payload)
                    // Note: This is a placeholder payload for the universal SetAll color command 
                    // on typical Logitech G keyboards over HID. 
                    // OmenCoreApp usually uses the 20-byte report ID 0x11
                    byte[] report = new byte[20];
                    report[0] = 0x11;
                    report[1] = 0xFF; // Device ID
                    report[2] = 0x0E; // RGB Command
                    report[3] = 0x3C; 
                    report[4] = 0x00;
                    report[5] = 0x01; // Color effect (Static)
                    report[6] = r;
                    report[7] = g;
                    report[8] = b;
                    
                    stream.Write(report, 0, report.Length);
                    Logger.WriteLine($"LogitechUsbBackend: Set color to R={r} G={g} B={b}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"LogitechUsbBackend.SetPerKeyColor: Error: {ex.Message}");
                return false;
            }
        }
    }
}
