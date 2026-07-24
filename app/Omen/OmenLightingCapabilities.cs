using System;

namespace OmenCore.Hardware;

public class OmenLightingCapabilities
{
	public bool HasKeyboardLighting { get; set; }

	public int KeyboardZoneCount { get; set; }

	public bool HasLightBar { get; set; }

	public int LightBarZoneCount { get; set; }

	public HpWmiBios.KbdType KeyboardType { get; set; }

	public bool IsFourZone => KeyboardType == HpWmiBios.KbdType.Standard || KeyboardType == HpWmiBios.KbdType.WithNumPad || KeyboardType == HpWmiBios.KbdType.TenKeyLess;

	public bool IsPerKey => KeyboardType == HpWmiBios.KbdType.PerKeyRgb || KeyboardType == HpWmiBios.KbdType.TenKeyLessPerKeyRgb;

	public int EffectiveKeyboardZoneCount => IsPerKey ? 1 : Math.Clamp((KeyboardZoneCount <= 0) ? 1 : KeyboardZoneCount, 1, 4);
}
