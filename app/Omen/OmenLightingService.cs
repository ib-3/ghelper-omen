using System;
using System.Drawing;
using OmenCore.Services;

namespace OmenCore.Hardware
{
    // ══════════════════════════════════════════════════════════════════════════
    // OMEN Lighting Service
    // Orchestrates keyboard & lightbar RGB via HP WMI BIOS commands.
    //
    // Keyboard path:   BiosCmd.Keyboard (0x20009 / 131081)
    //   Type 2 = GetColor,  Type 3 = SetColor   (128-byte colour table)
    //   Type 4 = GetBrightness, Type 5 = SetBrightness
    //   Type 6 = GetLedAnimation, Type 7 = SetLedAnimation
    //
    // Lightbar path:   BiosCmd.Default (0x20008 / 131080)
    //   Type 1 = GetPlatformSupport  → bit1 of byte[0]
    //   Type 4 = GetLightingRgb      → byte[0]=zoneCount, then R/G/B per zone
    //   Type 5 = SetLightingRgb      → 128-byte: byte[0]=zoneCount, R/G/B…
    //   Type 7 = SetBrightness       → {brightness, 0, 0, 0}
    //   Type 9 = SetAnimationMode    → animation payload (like keyboard type 7)
    //
    // OGH WMI_LedAnimation byte layout (for types 6/7 keyboard & type 9 lightbar):
    //   [0] Zone         (0xFF = all)
    //   [1] ColorMode    (0=Static 1=Breathing 2=ColorCycle 3=Wave 4=Strobe)
    //   [2] TimeHigh
    //   [3] TimeLow      (speed / period in tenths of a second)
    //   [4] Brightness   (0-100)
    //   [5] ColorCount   (number of user-defined colours in the payload)
    //   [6..] R,G,B…     (up to ~40 colours = 120 bytes)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Keyboard lighting effect types, matching OGH ColorMode values.
    /// </summary>
    public enum OmenLightingEffect : byte
    {
        /// <summary>Solid colour — no animation.</summary>
        Static = 0,

        /// <summary>Pulsing fade in/out.</summary>
        Breathing = 1,

        /// <summary>Full-spectrum rainbow cycle (auto-colour).</summary>
        ColorCycle = 2,

        /// <summary>Colour wave sweeping across zones.</summary>
        Wave = 3,

        /// <summary>Rapid strobe/flash.</summary>
        Strobe = 4,
    }

    /// <summary>
    /// Detected lighting capabilities for this machine, built by
    /// <see cref="OmenLightingService.ProbeAsync"/>.
    /// </summary>
    public class OmenLightingCapabilities
    {
        /// <summary>Whether WMI keyboard lighting commands work.</summary>
        public bool HasKeyboardLighting { get; set; }

        /// <summary>Number of independently addressable keyboard zones.</summary>
        public int KeyboardZoneCount { get; set; }

        /// <summary>Whether the machine has a front-edge light bar.</summary>
        public bool HasLightBar { get; set; }

        /// <summary>Number of independently addressable light-bar zones (0 if none).</summary>
        public int LightBarZoneCount { get; set; }

        /// <summary>Keyboard backlight type detected from BIOS.</summary>
        public HpWmiBios.KbdType KeyboardType { get; set; }

        /// <summary>Whether 4-zone RGB is available (standard OMEN).</summary>
        public bool IsFourZone => KeyboardType == HpWmiBios.KbdType.Standard
                               || KeyboardType == HpWmiBios.KbdType.WithNumPad
                               || KeyboardType == HpWmiBios.KbdType.TenKeyLess;

        /// <summary>Whether per-key RGB is available.</summary>
        public bool IsPerKey => KeyboardType == HpWmiBios.KbdType.PerKeyRgb
                             || KeyboardType == HpWmiBios.KbdType.TenKeyLessPerKeyRgb;
    }

    /// <summary>
    /// High-level lighting service — probes, caches, and applies all OMEN
    /// keyboard and lightbar effects through <see cref="HpWmiBios"/>.
    /// </summary>
    public class OmenLightingService
    {
        private readonly HpWmiBios _bios;
        private readonly LoggingService? _logging;

        public OmenLightingCapabilities Capabilities { get; private set; }
            = new OmenLightingCapabilities();

        public OmenLightingService(HpWmiBios bios, LoggingService? logging = null)
        {
            _bios = bios;
            _logging = logging;
        }

        public GHelper.Omen.Lighting.IOmenLightingBackend GetActiveBackend()
        {
            int method = AppConfig.Get("omen_rgb_method", 0);
            
            // Auto detection based on capabilities
            if (method == 0)
            {
                if (Capabilities.IsPerKey)
                    return new GHelper.Omen.Lighting.LogitechUsbBackend();
                
                return new GHelper.Omen.Lighting.WmiLightingBackend(_bios);
            }
            
            switch (method)
            {
                case 1: return new GHelper.Omen.Lighting.WmiLightingBackend(_bios);
                case 2: return new GHelper.Omen.Lighting.EcDirectBackend();
                case 3: return new GHelper.Omen.Lighting.LogitechUsbBackend();
                case 4: return new GHelper.Omen.Lighting.CorsairUsbBackend();
                case 5: return new GHelper.Omen.Lighting.OmenMonBackend();
                default: return new GHelper.Omen.Lighting.WmiLightingBackend(_bios);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Initialisation / auto-probe
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Auto-probe all lighting capabilities.  Call once at startup.
        /// </summary>
        public OmenLightingCapabilities Probe()
        {
            var caps = new OmenLightingCapabilities();

            if (!_bios.IsAvailable)
            {
                _logging?.Warn("OmenLightingService: WMI BIOS not available — no lighting support");
                Capabilities = caps;
                return caps;
            }

            // ── Keyboard type ───────────────────────────────────────────
            try
            {
                var kbdType = _bios.GetKeyboardType();
                if (kbdType.HasValue)
                {
                    caps.KeyboardType = kbdType.Value;
                    caps.HasKeyboardLighting = true;

                    var ctable = _bios.GetColorTable();
                    if (ctable != null && ctable.Length > 0 && ctable[0] > 0)
                    {
                        caps.KeyboardZoneCount = ctable[0];
                        if (caps.KeyboardZoneCount == 3 && !caps.IsPerKey)
                        {
                            _logging?.Info("OmenLightingService: Keyboard reported 3 zones, forcing to 4 for Omen standard keyboard.");
                            caps.KeyboardZoneCount = 4;
                        }
                    }
                    else
                    {
                        caps.KeyboardZoneCount = caps.IsFourZone ? 4 : (caps.IsPerKey ? 1 : 1);
                    }

                    _logging?.Info($"OmenLightingService: keyboard type = {kbdType.Value}, zones = {caps.KeyboardZoneCount}");
                }
                else
                {
                    _logging?.Info($"OmenLightingService: keyboard type unknown, checking HID...");
                    caps.KeyboardType = HpWmiBios.KbdType.Unknown;
                }

                // Override: if a Vendor HID device is present AND keyboard type is unknown, assume PerKey keyboard
                if (caps.KeyboardType == HpWmiBios.KbdType.Unknown)
                {
                    var hidSvc = new OmenHidLightingService(_logging);
                    if (hidSvc.HasPerKeyRgbDevice())
                    {
                        caps.KeyboardType = HpWmiBios.KbdType.PerKeyRgb;
                        caps.HasKeyboardLighting = true;
                        _logging?.Info("OmenLightingService: Vendor HID device detected — overriding to PerKeyRgb");
                    }
                    else
                    {
                        caps.KeyboardType = HpWmiBios.KbdType.TenKeyLess;
                        _logging?.Info("OmenLightingService: No HID device, falling back to TenKeyLess");
                    }
                }
            }
            catch (Exception ex)
            {
                _logging?.Warn($"OmenLightingService: keyboard probe error: {ex.Message}");
            }

            // ── Light bar ───────────────────────────────────────────────
            try
            {
                var (supported, zoneCount) = _bios.LightBarProbeSupport();
                caps.HasLightBar = supported;
                caps.LightBarZoneCount = zoneCount;
                _logging?.Info($"OmenLightingService: lightbar supported={supported}, zones={zoneCount}");
            }
            catch (Exception ex)
            {
                _logging?.Warn($"OmenLightingService: lightbar probe error: {ex.Message}");
            }

            Capabilities = caps;
            return caps;
        }

        // ──────────────────────────────────────────────────────────────────
        // Keyboard — 4-zone static colours
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Set all 4 keyboard zones to independent static colours.
        /// </summary>
        /// <param name="zones">Array of 1–4 colours; short arrays repeat last colour.</param>
        public bool SetKeyboardZoneColors(Color[] zones)
        {
            if (!Capabilities.HasKeyboardLighting) return false;

            var backend = GetActiveBackend();

            if (zones == null || zones.Length == 0)
                zones = new[] { Color.White };

            // Build R,G,B triplets for all zones
            byte[] zoneBytes = new byte[zones.Length * 3];
            for (int z = 0; z < zones.Length; z++)
            {
                Color c = zones[z];
                zoneBytes[z * 3]     = c.R;
                zoneBytes[z * 3 + 1] = c.G;
                zoneBytes[z * 3 + 2] = c.B;
            }

            bool ok = backend.SetColorTable(zoneBytes);
            
            // Fallback for Transcend 14 (OmenHidLightingService) if WMI fails or is standard PerKey
            if (!ok && Capabilities.IsPerKey)
            {
                var hid = new OmenHidLightingService(_logging);
                Color c = zones != null && zones.Length > 0 ? zones[0] : Color.White;
                ok = hid.SetStaticColor(c);
            }

            _logging?.Info($"SetKeyboardZoneColors (Backend: {backend.Name}): {(ok ? "OK" : "FAILED")}");
            return ok;
        }

        /// <summary>
        /// Set all keyboard zones to the same solid colour.
        /// </summary>
        public bool SetKeyboardSolidColor(Color color)
        {
            if (!Capabilities.HasKeyboardLighting) return false;
            int zones = Math.Max(4, Capabilities.KeyboardZoneCount);
            Color[] colors = new Color[zones];
            for (int i = 0; i < zones; i++) colors[i] = color;
            return SetKeyboardZoneColors(colors);
        }

        // ──────────────────────────────────────────────────────────────────
        // Keyboard — animation effects  (types 6/7 via BiosCmd.Keyboard)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply a lighting animation effect to the keyboard.
        /// </summary>
        /// <param name="effect">Effect type.</param>
        /// <param name="brightness">Brightness 0–100.</param>
        /// <param name="speed">Speed 0–10 (mapped to timing period).</param>
        /// <param name="colors">Up to 4 user-defined colours (null = auto/rainbow).</param>
        public bool SetKeyboardEffect(
            OmenLightingEffect effect,
            byte brightness = 100,
            byte speed = 5,
            Color[]? colors = null)
        {
            if (!Capabilities.HasKeyboardLighting) return false;

            if (Capabilities.IsPerKey)
            {
                var hid = new OmenHidLightingService(_logging);
                return hid.SetKeyboardEffect(effect, brightness, speed, colors);
            }

            byte[] payload = BuildAnimationPayload(0xFF, effect, brightness, speed, colors);
            bool ok = _bios.SetLedAnimation(payload);
            _logging?.Info($"SetKeyboardEffect({effect}, br={brightness}, sp={speed}): {(ok ? "OK" : "FAILED")}");
            return ok;
        }

        /// <summary>
        /// Apply a keyboard effect per-zone.
        /// Zone 0xFF = all zones; 0x00-0x03 = individual zones.
        /// </summary>
        public bool SetKeyboardEffectZone(
            byte zone,
            OmenLightingEffect effect,
            byte brightness = 100,
            byte speed = 5,
            Color[]? colors = null)
        {
            if (!Capabilities.HasKeyboardLighting) return false;

            byte[] payload = BuildAnimationPayload(zone, effect, brightness, speed, colors);
            return _bios.SetLedAnimation(payload);
        }

        // ──────────────────────────────────────────────────────────────────
        // Keyboard — brightness / power
        // ──────────────────────────────────────────────────────────────────

        public bool SetKeyboardBrightness(byte brightness)
            => _bios.SetBrightnessLevel(brightness);

        public int GetKeyboardBrightness()
            => _bios.GetBrightness();

        public bool SetKeyboardBacklight(bool on)
            => _bios.SetBacklight(on);

        // ──────────────────────────────────────────────────────────────────
        // Light bar — static colours
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Set the light bar to a solid colour across all zones.
        /// </summary>
        public bool SetLightBarSolidColor(Color color)
        {
            if (!Capabilities.HasLightBar) return false;
            int zones = Math.Max(1, Capabilities.LightBarZoneCount);
            Color[] colors = new Color[zones];
            for (int i = 0; i < zones; i++) colors[i] = color;
            return SetLightBarZoneColors(colors);
        }

        /// <summary>
        /// Set each light-bar zone to an independent colour.
        /// </summary>
        public bool SetLightBarZoneColors(Color[] zoneColors)
        {
            if (!Capabilities.HasLightBar || zoneColors == null || zoneColors.Length == 0)
                return false;

            bool ok = _bios.LightBarSetRgb(zoneColors);
            _logging?.Info($"SetLightBarZoneColors ({zoneColors.Length} zones): {(ok ? "OK" : "FAILED")}");
            return ok;
        }

        // ──────────────────────────────────────────────────────────────────
        // Light bar — animation effects  (type 9 via BiosCmd.Default)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply a lighting animation effect to the light bar.
        /// </summary>
        public bool SetLightBarEffect(
            OmenLightingEffect effect,
            byte brightness = 100,
            byte speed = 5,
            Color[]? colors = null)
        {
            if (!Capabilities.HasLightBar) return false;

            byte[] payload = BuildAnimationPayload(0xFF, effect, brightness, speed, colors);
            bool ok = _bios.LightBarSetAnimation(payload);
            _logging?.Info($"SetLightBarEffect({effect}): {(ok ? "OK" : "FAILED")}");
            return ok;
        }

        /// <summary>
        /// Set light-bar brightness (0–100).
        /// </summary>
        public bool SetLightBarBrightness(byte brightness)
            => _bios.LightBarSetBrightness(brightness);

        // ──────────────────────────────────────────────────────────────────
        // Shared helpers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the 128-byte animation payload used by both keyboard (type 7)
        /// and lightbar (type 9) WMI commands.
        /// </summary>
        /// <param name="zone">0xFF = all zones; 0x00-0x03 = per zone.</param>
        /// <param name="effect">Effect type (OGH ColorMode).</param>
        /// <param name="brightness">0–100.</param>
        /// <param name="speed">0–10 — converted to a timing period value.</param>
        /// <param name="colors">User colours; null = auto/palette.</param>
        internal static byte[] BuildAnimationPayload(
            byte zone,
            OmenLightingEffect effect,
            byte brightness,
            byte speed,
            Color[]? colors)
        {
            // OGH timing: slow≈60s, medium≈30s, fast≈10s — map 0-10 speed to period
            // period = 100 - (speed * 10), clamped 10-100, in tenths of a second
            ushort period = (ushort)Math.Clamp(100 - speed * 10, 10, 100);

            byte colorCount = 0;
            if (colors != null && colors.Length > 0 &&
                effect != OmenLightingEffect.ColorCycle)   // cycle uses auto-palette
            {
                colorCount = (byte)Math.Min(colors.Length, 40);
            }

            var payload = new byte[128];
            payload[0] = zone;
            payload[1] = (byte)effect;
            payload[2] = (byte)(period >> 8);
            payload[3] = (byte)(period & 0xFF);
            payload[4] = Math.Clamp(brightness, (byte)0, (byte)100);
            payload[5] = colorCount;

            if (colorCount > 0 && colors != null)
            {
                for (int i = 0; i < colorCount && (6 + i * 3 + 2) < 128; i++)
                {
                    payload[6 + i * 3]     = colors[i].R;
                    payload[6 + i * 3 + 1] = colors[i].G;
                    payload[6 + i * 3 + 2] = colors[i].B;
                }
            }

            return payload;
        }
    }
}
