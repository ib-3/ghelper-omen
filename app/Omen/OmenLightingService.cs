using System;
using GHelper.Helpers;
using System.Drawing;
using GHelper.Omen.Lighting;
using OmenCore.Services;

namespace OmenCore.Hardware;

public class OmenLightingService
{
	private readonly HpWmiBios _bios;
	private OmenAudioMeter? _audioMeter;
	private Color[]? _audioColors;
	private HidSharp.HidStream? _audioPulseStream;

	private readonly LoggingService? _logging;

	public OmenLightingCapabilities Capabilities { get; private set; } = new OmenLightingCapabilities();


	public OmenLightingService(HpWmiBios bios, LoggingService? logging = null)
	{
		_bios = bios;
		_logging = logging;
	}

	public IOmenLightingBackend GetActiveBackend()
	{
		switch (AppConfig.Get("omen_rgb_method", 0))
		{
		case 0:
			if (Capabilities.IsPerKey)
			{
				return new OmenHidLightingBackend(_logging);
			}
			return new WmiLightingBackend(_bios);
		case 1:
			return new WmiLightingBackend(_bios);
		case 2:
			return new EcDirectBackend();
		case 3:
			return new LogitechUsbBackend();
		case 4:
			return new CorsairUsbBackend();
		case 5:
			return new OmenMonBackend();
		default:
			return new WmiLightingBackend(_bios);
		}
	}


	public void RestoreLightingToDefault()
	{
		if (Capabilities.IsPerKey)
		{
			OmenHidLightingService hidService = new OmenHidLightingService(_logging);
			hidService.RestoreLightingToDefault();
		}
	}

	public OmenLightingCapabilities Probe()
	{
		OmenLightingCapabilities omenLightingCapabilities = new OmenLightingCapabilities();
		if (!_bios.IsAvailable)
		{
			_logging?.Warn("OmenLightingService: WMI BIOS not available — no lighting support");
			Capabilities = omenLightingCapabilities;
			return omenLightingCapabilities;
		}
		try
		{
			HpWmiBios.KbdType? keyboardType = _bios.GetKeyboardType();
			if (keyboardType.HasValue)
			{
				omenLightingCapabilities.KeyboardType = keyboardType.Value;
				omenLightingCapabilities.HasKeyboardLighting = true;
				byte[] colorTable = _bios.GetColorTable();
				omenLightingCapabilities.KeyboardZoneCount = DetectKeyboardZoneCount(colorTable, omenLightingCapabilities);
				_logging?.Info($"OmenLightingService: keyboard type = {keyboardType.Value}, zones = {omenLightingCapabilities.KeyboardZoneCount}");
			}
			else
			{
				_logging?.Info("OmenLightingService: keyboard type unknown, checking HID...");
				omenLightingCapabilities.KeyboardType = HpWmiBios.KbdType.Unknown;
			}
			if (omenLightingCapabilities.KeyboardType == HpWmiBios.KbdType.Unknown)
			{
				OmenHidLightingService omenHidLightingService = new OmenHidLightingService(_logging);
				if (omenHidLightingService.HasPerKeyRgbDevice())
				{
					omenLightingCapabilities.KeyboardType = HpWmiBios.KbdType.PerKeyRgb;
					omenLightingCapabilities.HasKeyboardLighting = true;
					_logging?.Info("OmenLightingService: Vendor HID device detected — overriding to PerKeyRgb");
				}
				else
				{
					omenLightingCapabilities.KeyboardType = HpWmiBios.KbdType.TenKeyLess;
					_logging?.Info("OmenLightingService: No HID device, falling back to TenKeyLess");
				}
			}
		}
		catch (Exception ex)
		{
			_logging?.Warn("OmenLightingService: keyboard probe error: " + ex.Message);
		}
		try
		{
			(bool supported, int zoneCount) tuple = _bios.LightBarProbeSupport();
			bool item = tuple.supported;
			int item2 = tuple.zoneCount;
			omenLightingCapabilities.HasLightBar = item;
			omenLightingCapabilities.LightBarZoneCount = item2;
			_logging?.Info($"OmenLightingService: lightbar supported={item}, zones={item2}");
		}
		catch (Exception ex2)
		{
			_logging?.Warn("OmenLightingService: lightbar probe error: " + ex2.Message);
		}
		Capabilities = omenLightingCapabilities;
		return omenLightingCapabilities;
	}

	private int DetectKeyboardZoneCount(byte[]? colorTable, OmenLightingCapabilities caps)
	{
		if (caps.IsPerKey)
		{
			return 1;
		}
		if (colorTable != null && colorTable.Length != 0 && colorTable[0] > 0)
		{
			byte b = colorTable[0];
			if (b == 3 && caps.IsFourZone)
			{
				_logging?.Info("OmenLightingService: Keyboard reported 3 as max zone index; treating as 4-zone RGB.");
				return 4;
			}
			return Math.Clamp((int)b, 1, 4);
		}
		return (!caps.IsFourZone) ? 1 : 4;
	}

	public bool SetKeyboardZoneColors(Color[] zones)
	{
		if (!Capabilities.HasKeyboardLighting)
		{
			return false;
		}
		IOmenLightingBackend activeBackend = GetActiveBackend();
		if (zones == null || zones.Length == 0)
		{
			zones = new Color[1] { Color.White };
		}
		float b = GetKeyboardBrightness() / 100f;
		byte[] array = new byte[zones.Length * 3];
		for (int i = 0; i < zones.Length; i++)
		{
			Color color = zones[i];
			array[i * 3] = (byte)(color.R * b);
			array[i * 3 + 1] = (byte)(color.G * b);
			array[i * 3 + 2] = (byte)(color.B * b);
		}
		bool flag = activeBackend.SetColorTable(array);
		_logging?.Info("SetKeyboardZoneColors (Backend: " + activeBackend.Name + "): " + (flag ? "OK" : "FAILED"));
		return flag;
	}

	public bool SetKeyboardPerKeyColors(Color[] keyColors)
	{
		if (!Capabilities.HasKeyboardLighting || !Capabilities.IsPerKey)
		{
			return false;
		}
		float b = GetKeyboardBrightness() / 100f;
		Color[] scaledColors = new Color[keyColors.Length];
		for (int i = 0; i < keyColors.Length; i++)
		{
			scaledColors[i] = Color.FromArgb(keyColors[i].A, (int)(keyColors[i].R * b), (int)(keyColors[i].G * b), (int)(keyColors[i].B * b));
		}
		OmenHidLightingService omenHidLightingService = new OmenHidLightingService(_logging);
		bool flag = omenHidLightingService.SetKeyColors(scaledColors);
		_logging?.Info($"SetKeyboardPerKeyColors ({keyColors.Length} keys): {(flag ? "OK" : "FAILED")}");
		return flag;
	}

	public bool SetKeyboardSolidColor(Color color)
	{
		if (!Capabilities.HasKeyboardLighting)
		{
			return false;
		}
		int effectiveKeyboardZoneCount = Capabilities.EffectiveKeyboardZoneCount;
		Color[] array = new Color[effectiveKeyboardZoneCount];
		for (int i = 0; i < effectiveKeyboardZoneCount; i++)
		{
			array[i] = color;
		}
		return SetKeyboardZoneColors(array);
	}

	public bool SetKeyboardEffect(OmenLightingEffect effect, byte brightness = 100, byte speed = 5, byte direction = 0, byte size = 0, Color[]? colors = null)
	{
		if (!Capabilities.HasKeyboardLighting)
		{
			return false;
		}
		
		if (effect == OmenLightingEffect.AudioPulse)
		{
			if (_audioMeter == null)
			{
				_audioMeter = new OmenAudioMeter();
				_audioMeter.OnVolumeUpdated += (vol) => {
					if (Capabilities.IsPerKey && _audioPulseStream != null)
					{
						try {
							OmenHidLightingService omenHidLightingService = new OmenHidLightingService(_logging);
							omenHidLightingService.SetAudioPulseVolume(_audioPulseStream, (byte)(vol * 100), _audioColors);
						} catch { }
					}
				};
			}
			
			if (_audioPulseStream == null && Capabilities.IsPerKey)
			{
				OmenHidLightingService omenHidLightingService = new OmenHidLightingService(_logging);
				_audioPulseStream = omenHidLightingService.OpenStream();
			}

			_audioColors = colors;
			_audioMeter.Start();
		}
		else
		{
			if (_audioMeter != null)
			{
				_audioMeter.Stop();
			}
			if (_audioPulseStream != null)
			{
				try { _audioPulseStream.Dispose(); } catch { }
				_audioPulseStream = null;
			}
		}

		if (Capabilities.IsPerKey)
		{
			OmenHidLightingService omenHidLightingService = new OmenHidLightingService(_logging);
			return omenHidLightingService.SetKeyboardEffect(effect, brightness, speed, direction, size, colors);
		}
		byte[] ledAnimation = BuildAnimationPayload(byte.MaxValue, effect, brightness, speed, colors);
		bool flag = _bios.SetLedAnimation(ledAnimation);
		_logging?.Info($"SetKeyboardEffect({effect}, br={brightness}, sp={speed}): {(flag ? "OK" : "FAILED")}");
		return flag;
	}

	public bool SetKeyboardEffectZone(byte zone, OmenLightingEffect effect, byte brightness = 100, byte speed = 5, Color[]? colors = null)
	{
		if (!Capabilities.HasKeyboardLighting)
		{
			return false;
		}
		byte[] ledAnimation = BuildAnimationPayload(zone, effect, brightness, speed, colors);
		return _bios.SetLedAnimation(ledAnimation);
	}

	public bool SetKeyboardBrightness(byte brightness)
	{
		return _bios.SetBrightnessLevel(brightness);
	}

	public int GetKeyboardBrightness()
	{
		return _bios.GetBrightness();
	}

	public bool SetKeyboardBacklight(bool on)
	{
		return _bios.SetBacklight(on);
	}

	public bool SetLightBarSolidColor(Color color)
	{
		if (!Capabilities.HasLightBar)
		{
			return false;
		}
		int num = Math.Max(1, Capabilities.LightBarZoneCount);
		Color[] array = new Color[num];
		for (int i = 0; i < num; i++)
		{
			array[i] = color;
		}
		return SetLightBarZoneColors(array);
	}

	public bool SetLightBarZoneColors(Color[] zoneColors)
	{
		if (!Capabilities.HasLightBar || zoneColors == null || zoneColors.Length == 0)
		{
			return false;
		}
		bool flag = _bios.LightBarSetRgb(zoneColors);
		_logging?.Info($"SetLightBarZoneColors ({zoneColors.Length} zones): {(flag ? "OK" : "FAILED")}");
		return flag;
	}

	public bool SetLightBarEffect(OmenLightingEffect effect, byte brightness = 100, byte speed = 5, Color[]? colors = null)
	{
		if (!Capabilities.HasLightBar)
		{
			return false;
		}
		byte[] animationPayload = BuildAnimationPayload(byte.MaxValue, effect, brightness, speed, colors);
		bool flag = _bios.LightBarSetAnimation(animationPayload);
		_logging?.Info($"SetLightBarEffect({effect}): {(flag ? "OK" : "FAILED")}");
		return flag;
	}

	public bool SetLightBarBrightness(byte brightness)
	{
		return _bios.LightBarSetBrightness(brightness);
	}

	internal static byte[] BuildAnimationPayload(byte zone, OmenLightingEffect effect, byte brightness, byte speed, Color[]? colors)
	{
		int num = speed * 5;
		ushort num2 = (ushort)Math.Clamp(100 - num * 10, 10, 100);
		byte b = 0;
		if (colors != null && colors.Length != 0 && effect != OmenLightingEffect.ColorCycle)
		{
			b = (byte)Math.Min(colors.Length, 40);
		}
		byte[] array = new byte[128];
		array[0] = zone;
		array[1] = (byte)effect;
		array[2] = (byte)(num2 >> 8);
		array[3] = (byte)(num2 & 0xFFu);
		array[4] = Math.Clamp(brightness, (byte)0, (byte)100);
		array[5] = b;
		if (b > 0 && colors != null)
		{
			for (int i = 0; i < b && 6 + i * 3 + 2 < 128; i++)
			{
				array[6 + i * 3] = colors[i].R;
				array[6 + i * 3 + 1] = colors[i].G;
				array[6 + i * 3 + 2] = colors[i].B;
			}
		}
		return array;
	}
}
