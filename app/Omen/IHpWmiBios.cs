using System.Drawing;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Minimal interface to abstract HP WMI BIOS interactions used by fan controller.
    /// Allows unit tests to inject a fake implementation.
    /// </summary>
    public interface IHpWmiBios
    {
        bool IsAvailable { get; }
        string Status { get; }
        HpWmiBios.ThermalPolicyVersion ThermalPolicy { get; }
        int FanCount { get; }
        int MaxFanLevel { get; }

        (int fan1Rpm, int fan2Rpm)? GetFanRpmDirect();
        (byte fan1, byte fan2)? GetFanLevel();

        bool? GetFanMax();
        bool SetFanMax(bool enabled);
        bool SetFanLevel(byte fan1, byte fan2);
        bool SetFanMode(HpWmiBios.FanMode mode);

        double? GetTemperature();
        double? GetGpuTemperature();
        void ExtendFanCountdown();

        (bool customTgp, bool ppab, int dState)? GetGpuPower();
        bool SetGpuPower(HpWmiBios.GpuPowerLevel level, int tempTarget = 0, int dState = 1);
        HpWmiBios.GpuMode? GetGpuMode();

        // ── Keyboard lighting ─────────────────────────────────────────────
        HpWmiBios.KbdType? GetKeyboardType();
        bool HasBacklight();
        bool SetBacklight(bool enabled);
        int GetBrightness();
        bool SetBrightnessLevel(byte brightness);
        byte[]? GetColorTable();
        bool SetColorTable(byte[] zoneColors);
        bool SetZoneColor(int zone, byte r, byte g, byte b);
        byte[]? GetLedAnimation();
        bool SetLedAnimation(byte[] animationData);

        // ── Light bar ─────────────────────────────────────────────────────
        (bool supported, int zoneCount) LightBarProbeSupport();
        byte[]? LightBarGetRgb();
        bool LightBarSetRgb(Color[] zoneColors);
        bool LightBarSetStaticColor(Color color, int zoneCount = 1);
        bool LightBarSetBrightness(byte brightness);
        bool LightBarSetAnimation(byte[] animationPayload);

        void Dispose();
    }
}
