namespace OmenCore.Hardware;

public static class OmenLightingEffectExtensions
{
	public static bool IsHidOnly(this OmenLightingEffect effect)
	{
		return (int)effect >= 10;
	}

	public static byte ToHidEffectType(this OmenLightingEffect effect)
	{
		if (1 == 0)
		{
		}
		byte result = effect switch
		{
			OmenLightingEffect.Static => 1, 
			OmenLightingEffect.Breathing => 2, 
			OmenLightingEffect.Starlight => 3, 
			OmenLightingEffect.ColorCycle => 4, 
			OmenLightingEffect.Ghosting => 6, 
			OmenLightingEffect.Ripple => 7, 
			OmenLightingEffect.Wave => 8, 
			OmenLightingEffect.OmenX => (byte)(AppConfig.Is("omen_x_alt_id") ? 13 : 9), 
			OmenLightingEffect.Raindrop => 10, 
			OmenLightingEffect.AudioPulse => 11, 
			OmenLightingEffect.Confetti => 12, 
			OmenLightingEffect.Sun => 13, 
			OmenLightingEffect.Swipe => 14, 
			_ => 1, 
		};
		if (1 == 0)
		{
		}
		return result;
	}
}
