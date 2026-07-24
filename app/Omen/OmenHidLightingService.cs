using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using HidSharp;
using OmenCore.Services;

namespace OmenCore.Hardware;

public class OmenHidLightingService
{
	private readonly LoggingService? _logging;

	private static readonly int[] OMEN_VENDOR_IDS = new int[4] { 3426, 1121, 8137, 1008 };

	private const int REPORT_SIZE = 65;

	public OmenHidLightingService(LoggingService? logging = null)
	{
		_logging = logging;
	}

	private HidDevice? FindVendorDevice()
	{
		int[] oMEN_VENDOR_IDS = OMEN_VENDOR_IDS;
		foreach (int value in oMEN_VENDOR_IDS)
		{
			IEnumerable<HidDevice> hidDevices = DeviceList.Local.GetHidDevices(value);
			foreach (HidDevice item in hidDevices)
			{
				try
				{
					if (item.GetMaxOutputReportLength() == 65)
					{
						return item;
					}
				}
				catch
				{
				}
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
		int num4 = ((num3 > data.Length) ? (data.Length - num2) : (num3 - num2));
		byte[] array = new byte[65];
		array[0] = 0;
		array[1] = commandType;
		array[2] = page;
		array[3] = (byte)num4;
		array[4] = 0;
		if (num4 > 0)
		{
			Array.Copy(data, num2, array, 5, num4);
		}
		return array;
	}


	public void RestoreLightingToDefault()
	{
		HidDevice hidDevice = FindVendorDevice();
		if (hidDevice == null)
		{
			return;
		}
		try
		{
			using HidStream hidStream = hidDevice.Open();
			byte[] array = new byte[65];
			array[0] = 0;
			array[1] = 16;
			array[2] = 7;
			array[3] = 4;
			array[4] = 0;
			hidStream.Write(array, 0, array.Length);
		}
		catch { }
	}

	private void SetUserModeEnable(HidStream stream)
	{
		byte[] array = new byte[65];
		array[0] = 0;
		array[1] = 128;
		array[2] = 0;
		array[3] = 0;
		array[4] = 0;
		array[5] = 165;
		array[6] = 90;
		stream.Write(array, 0, array.Length);
		Thread.Sleep(10);
	}

	private void StoreLightingEffectDataToFlash(HidStream stream)
	{
		byte[] array = new byte[65];
		array[0] = 0;
		array[1] = 10;
		array[2] = 0;
		array[3] = 2;
		array[4] = 0;
		array[5] = 172;
		array[6] = 83;
		stream.Write(array, 0, array.Length);
		Thread.Sleep(10);
	}

	private static byte GetHardwareEffectId(OmenLightingEffect effect)
	{
		if (1 == 0)
		{
		}
		byte result = effect switch
		{
			OmenLightingEffect.Static => 1, 
			OmenLightingEffect.Breathing => 2, 
			OmenLightingEffect.ColorCycle => 4, 
			OmenLightingEffect.Starlight => 7, 
			OmenLightingEffect.Ghosting => 8, 
			OmenLightingEffect.Ripple => 9, 
			OmenLightingEffect.Wave => 10, 
			OmenLightingEffect.Raindrop => 12, 
			OmenLightingEffect.AudioPulse => 14, 
			OmenLightingEffect.Confetti => 15, 
			OmenLightingEffect.Sun => 16, 
			OmenLightingEffect.Swipe => 17, 
			OmenLightingEffect.OmenX => (byte)AppConfig.Get("omen_x_alt_id", 13),
			_ => 1, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	public HidStream? OpenStream()
	{
		HidDevice hidDevice = FindVendorDevice();
		return hidDevice?.Open();
	}

	public bool SetAudioPulseVolume(HidStream hidStream, byte brightness, Color[]? colors)
	{
		try
		{
			byte[] array = new byte[65];
			array[0] = 0;
			array[1] = 3;
			array[2] = 0;
			array[3] = 36;
			array[4] = 0;
			array[5] = 14;
			array[6] = 0;
			array[7] = (byte)Math.Min((colors == null) ? 1 : colors.Length, 2);
			array[8] = 0;
			array[9] = 100;
			array[10] = 0;
			array[11] = 0;
			array[12] = 0;
			array[13] = brightness;
			array[14] = brightness;
			for (int i = 0; i < 4; i++)
			{
				Color color = ((colors != null && i < colors.Length) ? colors[i] : Color.Black);
				array[29 + i * 3] = color.R;
				array[30 + i * 3] = color.G;
				array[31 + i * 3] = color.B;
			}
			hidStream.Write(array, 0, array.Length);
			return true;
		}
		catch (Exception ex)
		{
			_logging?.Error("SetAudioPulseVolume error: " + ex.Message);
			return false;
		}
	}

	public bool SetKeyboardEffect(OmenLightingEffect effect, byte brightness, byte speed, byte direction, byte size, Color[]? colors)
	{
		HidDevice hidDevice = FindVendorDevice();
		if (hidDevice == null)
		{
			return false;
		}
		try
		{
			using HidStream hidStream = hidDevice.Open();
			SetUserModeEnable(hidStream);
			byte[] array = new byte[65];
			array[0] = 0;
			array[1] = 3;
			array[2] = 0;
			array[3] = 36;
			array[4] = 0;
			byte hardwareEffectId = GetHardwareEffectId(effect);
			byte b = 1;
			byte b2 = 0;
			byte b3 = 0;
			byte b4 = 0;
			switch (effect)
			{
			case OmenLightingEffect.Static:
				b = 1;
				break;
			case OmenLightingEffect.Breathing:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 2);
				break;
			case OmenLightingEffect.Starlight:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 2);
				break;
			case OmenLightingEffect.ColorCycle:
			case OmenLightingEffect.OmenX:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 4);
				break;
			case OmenLightingEffect.Ghosting:
				b = 1;
				break;
			case OmenLightingEffect.Ripple:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 4);
				b3 = size;
				break;
			case OmenLightingEffect.Wave:
				b = 0;
				b2 = direction;
				break;
			case OmenLightingEffect.Swipe:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 4);
				b2 = direction;
				break;
			case OmenLightingEffect.Raindrop:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 2);
				b4 = size;
				break;
			case OmenLightingEffect.AudioPulse:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 2);
				break;
			case OmenLightingEffect.Confetti:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 4);
				break;
			case OmenLightingEffect.Sun:
				b = (byte)Math.Min((colors == null) ? 1 : colors.Length, 2);
				break;
			}
			byte b5 = 4;
			byte b6 = 0;
			if (b == 0)
			{
				b5 = 4;
				b6 = 5;
			}
			else
			{
				b5 = (byte)(b - 1);
				b6 = ((b != 1) ? ((byte)1) : ((byte)0));
			}
			if (1 == 0)
			{
			}
			byte b7 = ((brightness <= 33) ? ((brightness > 0) ? ((byte)1) : ((byte)0)) : ((byte)((brightness > 66) ? 3 : 2)));
			if (1 == 0)
			{
			}
			byte b8 = b7;
			array[5] = hardwareEffectId;
			array[6] = b6;
			array[7] = b5;
			array[8] = speed;
			array[9] = b8;
			array[10] = b2;
			array[11] = b3;
			array[12] = b4;
			if (effect == OmenLightingEffect.AudioPulse)
			{
				array[13] = brightness;
				array[14] = brightness;
			}
			if (colors != null && b > 0)
			{
				float bMult = brightness / 100f;
				for (int i = 0; i < Math.Min(colors.Length, b); i++)
				{
					array[29 + i * 3] = (byte)(colors[i].R * bMult);
					array[30 + i * 3] = (byte)(colors[i].G * bMult);
					array[31 + i * 3] = (byte)(colors[i].B * bMult);
				}
			}
			hidStream.Write(array, 0, array.Length);
			Thread.Sleep(10);
			StoreLightingEffectDataToFlash(hidStream);
			return true;
		}
		catch (Exception ex)
		{
			_logging?.Warn("OmenHidLightingService.SetEffect: Error: " + ex.Message);
			return false;
		}
	}

	public bool SetStaticColor(Color color)
	{
		Color[] array = new Color[144];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = color;
		}
		return SetKeyColors(array);
	}

	public bool SetPerKeyColor(int keyId, Color color)
	{
		if (keyId < 0 || keyId >= 144)
		{
			return false;
		}
		Color[] array = new Color[144];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = Color.Black;
		}
		array[keyId] = color;
		return SetKeyColors(array);
	}

	public bool SetKeyColors(Color[] colors)
	{
		HidDevice hidDevice = FindVendorDevice();
		if (hidDevice == null)
		{
			_logging?.Info("OmenHidLightingService: No Vendor HID device found.");
			return false;
		}
		try
		{
			using HidStream hidStream = hidDevice.Open();
			SetUserModeEnable(hidStream);
			int num = 144;
			byte[] array = new byte[num];
			byte[] array2 = new byte[num];
			byte[] array3 = new byte[num];
			for (int i = 0; i < num; i++)
			{
				Color color = ((i < colors.Length) ? colors[i] : Color.Black);
				array[i] = color.R;
				array2[i] = color.G;
				array3[i] = color.B;
			}
			for (byte b = 0; b < 3; b++)
			{
				hidStream.Write(CreateStaticCmd(5, b, array));
			}
			for (byte b2 = 0; b2 < 3; b2++)
			{
				hidStream.Write(CreateStaticCmd(6, b2, array2));
			}
			for (byte b3 = 0; b3 < 3; b3++)
			{
				hidStream.Write(CreateStaticCmd(7, b3, array3));
			}
			byte[] array4 = new byte[65];
			array4[0] = 0;
			array4[1] = 3;
			array4[2] = 0;
			array4[3] = 36;
			array4[4] = 0;
			array4[5] = 5;
			array4[6] = 0;
			array4[7] = 1;
			array4[8] = 5;
			hidStream.Write(array4, 0, array4.Length);
			Thread.Sleep(10);
			StoreLightingEffectDataToFlash(hidStream);
			_logging?.Info($"OmenHidLightingService: Set {Math.Min(colors.Length, num)} key color(s)");
			return true;
		}
		catch (Exception ex)
		{
			_logging?.Warn("OmenHidLightingService: Error: " + ex.Message);
			return false;
		}
	}
}
