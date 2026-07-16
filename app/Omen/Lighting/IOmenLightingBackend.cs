namespace GHelper.Omen.Lighting
{
    public enum OmenRgbMethod
    {
        Auto = 0,
        Wmi = 1,
        EcDirect = 2,
        LogitechUsb = 3,
        CorsairUsb = 4,
        OmenMon = 5
    }

    public interface IOmenLightingBackend
    {
        string Name { get; }
        OmenRgbMethod Method { get; }
        bool IsAvailable { get; }
        bool IsPerKey { get; }
        int ZoneCount { get; }

        bool SetColorTable(byte[] zoneColors);
        bool SetPerKeyColor(int keyId, byte r, byte g, byte b);
    }
}
