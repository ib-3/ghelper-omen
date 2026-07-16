using System;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using GHelper.Omen;

namespace GHelper.Omen.Lighting
{
    public class OmenMonBackend : IOmenLightingBackend
    {
        public string Name => "OmenMon CLI";
        public OmenRgbMethod Method => OmenRgbMethod.OmenMon;
        
        public bool IsAvailable => true; // Available if user has OmenMon installed in PATH or directory
        public bool IsPerKey => false;
        public int ZoneCount => 4;

        public OmenMonBackend()
        {
        }

        public bool SetColorTable(byte[] zoneColors)
        {
            if (zoneColors == null || zoneColors.Length < 12) return false;

            try
            {
                // Convert bytes to hex string format: RRGGBB:RRGGBB:RRGGBB:RRGGBB
                string colorArg = string.Format("{0:X2}{1:X2}{2:X2}:{3:X2}{4:X2}{5:X2}:{6:X2}{7:X2}{8:X2}:{9:X2}{10:X2}{11:X2}",
                    zoneColors[0], zoneColors[1], zoneColors[2],
                    zoneColors[3], zoneColors[4], zoneColors[5],
                    zoneColors[6], zoneColors[7], zoneColors[8],
                    zoneColors[9], zoneColors[10], zoneColors[11]);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "OmenMon.exe",
                    Arguments = $"-Bios Color={colorArg}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(2000);
                    Logger.WriteLine($"OmenMonBackend: Executed OmenMon.exe -Bios Color={colorArg}");
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"OmenMonBackend: Failed to execute OmenMon: {ex.Message}");
                return false;
            }
        }

        public bool SetPerKeyColor(int keyId, byte r, byte g, byte b)
        {
            return false;
        }
    }
}
