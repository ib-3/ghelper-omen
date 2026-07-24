using System.Drawing;
using OmenCore.Hardware;
using OmenCore.Services;

namespace GHelper.Omen.Lighting;

public class OmenHidLightingBackend : IOmenLightingBackend
{
	private readonly OmenHidLightingService _hid;

	public string Name => "OMEN HID (Per-Key)";

	public OmenRgbMethod Method => OmenRgbMethod.Auto;

	public bool IsAvailable => _hid.HasPerKeyRgbDevice();

	public bool IsPerKey => true;

	public int ZoneCount => 144;

	public OmenHidLightingBackend(LoggingService? logging = null)
	{
		_hid = new OmenHidLightingService(logging);
	}

	public bool SetColorTable(byte[] zoneColors)
	{
		if (zoneColors == null || zoneColors.Length < 3)
		{
			return false;
		}
		return _hid.SetStaticColor(Color.FromArgb(zoneColors[0], zoneColors[1], zoneColors[2]));
	}

	public bool SetPerKeyColor(int keyId, byte r, byte g, byte b)
	{
		return _hid.SetPerKeyColor(keyId, Color.FromArgb(r, g, b));
	}
}
