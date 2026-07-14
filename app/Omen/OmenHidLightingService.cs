using System;
using System.Linq;
using System.Drawing;
using System.Threading;
using HidSharp;
using OmenCore.Services;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Controls OMEN keyboard RGB lighting via HP's vendor-specific HID protocol
    /// (Report ID 0, 65 bytes Output). This is used by the OMEN Transcend 14 and others.
    /// </summary>
    public class OmenHidLightingService
    {
        private readonly LoggingService? _logging;
        private static readonly int[] OMEN_VENDOR_IDS = {
            0x0D62, // Darfon
            0x0461, // Primax
            0x1FC9, // NXP
            0x03F0  // HP
        };

        private const int REPORT_SIZE = 65; // 1 byte Report ID + 64 bytes data

        public OmenHidLightingService(LoggingService? logging = null)
        {
            _logging = logging;
        }

        /// <summary>
        /// Find the HID device that accepts 65-byte Output Reports (HP Vendor Protocol).
        /// </summary>
        private HidDevice? FindVendorDevice()
        {
            foreach (int vid in OMEN_VENDOR_IDS)
            {
                var devices = DeviceList.Local.GetHidDevices(vid);
                foreach (var dev in devices)
                {
                    try
                    {
                        if (dev.GetMaxOutputReportLength() == REPORT_SIZE)
                        {
                            return dev;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        public bool HasPerKeyRgbDevice()
        {
            return FindVendorDevice() != null;
        }

        private byte[] CreateStaticCmd(byte commandType, byte page, byte[] data)
        {
            int num = 60;
            int num2 = page * num;
            int num3 = (page + 1) * num;
            int num4 = (num3 > data.Length) ? (data.Length - num2) : (num3 - num2);

            byte[] report = new byte[REPORT_SIZE];
            report[0] = 0; // Report ID
            report[1] = commandType;
            report[2] = page;
            report[3] = (byte)num4;
            report[4] = 0;
            
            if (num4 > 0)
            {
                Array.Copy(data, num2, report, 5, num4);
            }
            
            return report;
        }

        private void SetUserModeEnable(HidStream stream)
        {
            byte[] report = new byte[REPORT_SIZE];
            report[0] = 0;
            report[1] = 128;
            report[2] = 0;
            report[3] = 0;
            report[4] = 0;
            report[5] = 165;
            report[6] = 90;
            stream.Write(report, 0, report.Length);
            Thread.Sleep(10); // Small delay to let device process
        }

        private void StoreLightingEffectDataToFlash(HidStream stream)
        {
            byte[] report = new byte[REPORT_SIZE];
            report[0] = 0;
            report[1] = 10;
            report[2] = 0;
            report[3] = 2;
            report[4] = 0;
            report[5] = 172;
            report[6] = 83;
            stream.Write(report, 0, report.Length);
            Thread.Sleep(10);
        }

        private void SetKeyboardBrightness(HidStream stream, byte brightness)
        {
            byte[] report = new byte[REPORT_SIZE];
            report[0] = 0;
            report[1] = 12;
            report[2] = 0;
            report[3] = 1;
            report[4] = 0;
            report[5] = brightness;
            stream.Write(report, 0, report.Length);
            Thread.Sleep(10);
        }

        public bool SetKeyboardEffect(OmenLightingEffect effect, byte brightness, byte speed, Color[]? colors)
        {
            var device = FindVendorDevice();
            if (device == null) return false;

            try
            {
                using (var stream = device.Open())
                {
                    byte[] report = new byte[REPORT_SIZE];
                    report[0] = 0;
                    report[1] = 3;   // Command = SetLightingEffect
                    report[2] = 0;   // EffectTargetIndex = ALL_LED_AREA
                    report[3] = 36;  // Length
                    report[4] = 0;

                    // Map effect
                    byte effectType = 1; // STEADY
                    byte showMode = 0;
                    byte colorCount = 1;
                    
                    if (effect == OmenLightingEffect.Static) { effectType = 1; }
                    else if (effect == OmenLightingEffect.Breathing) { effectType = 2; colorCount = (byte)(colors?.Length ?? 1); }
                    else if (effect == OmenLightingEffect.ColorCycle) { effectType = 4; colorCount = 0; }

                    report[5] = effectType;
                    report[6] = showMode;
                    report[7] = colorCount;
                    report[8] = speed;
                    report[10] = 0; // Direction
                    report[11] = 0; // RippleSize
                    report[12] = 0; // RaindropFreq

                    // Colors at offset 25 (Raw[30])
                    if (colors != null)
                    {
                        for (int i = 0; i < Math.Min(colors.Length, 4); i++)
                        {
                            report[30 + (i * 3)] = colors[i].R;
                            report[31 + (i * 3)] = colors[i].G;
                            report[32 + (i * 3)] = colors[i].B;
                        }
                    }

                    stream.Write(report, 0, report.Length);
                    Thread.Sleep(10);

                    SetKeyboardBrightness(stream, brightness);
                    StoreLightingEffectDataToFlash(stream);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logging?.Warn($"OmenHidLightingService.SetEffect: Error: {ex.Message}");
                return false;
            }
        }

        public bool SetStaticColor(Color color)
        {
            var device = FindVendorDevice();
            if (device == null)
            {
                _logging?.Info("OmenHidLightingService: No Vendor HID device found.");
                return false;
            }

            try
            {
                using (var stream = device.Open())
                {
                    SetUserModeEnable(stream);

                    // We send up to 144 keys across 3 pages (0, 1, 2)
                    int totalKeys = 144;
                    byte[] rMap = new byte[totalKeys];
                    byte[] gMap = new byte[totalKeys];
                    byte[] bMap = new byte[totalKeys];

                    for (int i = 0; i < totalKeys; i++)
                    {
                        rMap[i] = color.R;
                        gMap[i] = color.G;
                        bMap[i] = color.B;
                    }

                    // Send R pages
                    for (byte p = 0; p < 3; p++)
                        stream.Write(CreateStaticCmd(5, p, rMap));

                    // Send G pages
                    for (byte p = 0; p < 3; p++)
                        stream.Write(CreateStaticCmd(6, p, gMap));

                    // Send B pages
                    for (byte p = 0; p < 3; p++)
                        stream.Write(CreateStaticCmd(7, p, bMap));

                    // Set effect to 5 (Static DPI / Custom RAM map) or 1 (Steady)
                    // Let's try 5 for custom map, or 1 for steady
                    byte[] effectReport = new byte[REPORT_SIZE];
                    effectReport[0] = 0;
                    effectReport[1] = 3;   // SetLightingEffect
                    effectReport[2] = 0;   // ALL_LED_AREA
                    effectReport[3] = 36;  // Length
                    effectReport[4] = 0;
                    effectReport[5] = 5;   // STATIC_DPI_COLOR (custom)
                    effectReport[6] = 0;   // ShowMode
                    effectReport[7] = 1;   // ColorNumber
                    effectReport[8] = 5;   // LedSpeed
                    stream.Write(effectReport, 0, effectReport.Length);
                    Thread.Sleep(10);

                    // Max brightness
                    SetKeyboardBrightness(stream, 100);

                    // Store and apply
                    StoreLightingEffectDataToFlash(stream);

                    _logging?.Info($"OmenHidLightingService: Set all keys to R={color.R} G={color.G} B={color.B}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logging?.Warn($"OmenHidLightingService: Error: {ex.Message}");
                return false;
            }
        }
    }
}
