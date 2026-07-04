using System;
using System.Linq;

namespace OmenCore.Models
{
    /// <summary>
    /// Canonical fan-mode name aliases used across view models.
    /// Keeps tray, startup-restore, and fan-page mode mapping consistent.
    /// </summary>
    public static class FanModeNameResolver
    {
        public static bool IsCustomAlias(string? value)
        {
            return ContainsAliasToken(value, "custom", "manual");
        }

        public static bool IsMaxAlias(string? value)
        {
            return ContainsAliasToken(value, "max", "maximum");
        }

        public static bool IsQuietAlias(string? value)
        {
            return ContainsAliasToken(value, "quiet", "silent", "cool", "battery");
        }

        public static bool IsAutoAlias(string? value)
        {
            return ContainsAliasToken(value, "auto", "balanced", "default");
        }

        public static bool IsPerformanceAlias(string? value)
        {
            return ContainsAliasToken(value, "performance", "turbo", "extreme", "gaming", "boost");
        }

        public static string ResolveGeneralProfileFromPresetName(string? presetName)
        {
            if (IsMaxAlias(presetName) || IsPerformanceAlias(presetName))
            {
                return "Performance";
            }

            if (IsQuietAlias(presetName))
            {
                return "Quiet";
            }

            if (IsAutoAlias(presetName))
            {
                return "Balanced";
            }

            if (IsCustomAlias(presetName))
            {
                return "Custom";
            }

            return "Custom";
        }

        public static FanMode ResolveBuiltInFanMode(string? value)
        {
            if (IsMaxAlias(value)) return FanMode.Max;
            if (IsQuietAlias(value)) return FanMode.Quiet;
            if (IsPerformanceAlias(value)) return FanMode.Performance;
            if (IsCustomAlias(value)) return FanMode.Manual;
            return FanMode.Auto;
        }

        public static string ResolveCardMode(FanPreset preset)
        {
            if (preset == null)
            {
                return "Auto";
            }

            if (IsMaxAlias(preset.Name)) return "Max";

            var token = Normalize(preset.Name);
            if (token is "extreme") return "Extreme";
            if (token is "gaming") return "Gaming";
            if (IsQuietAlias(token)) return "Silent";
            if (IsAutoAlias(token)) return "Auto";

            return preset.Mode switch
            {
                FanMode.Manual => "Custom",
                FanMode.Max => "Max",
                FanMode.Quiet => "Silent",
                FanMode.Auto => "Auto",
                _ => "Auto"
            };
        }

        private static string Normalize(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool ContainsAliasToken(string? value, params string[] aliases)
        {
            var normalized = Normalize(value);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            if (aliases.Contains(normalized, StringComparer.Ordinal))
            {
                return true;
            }

            var tokens = normalized
                .Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']', '/' }, StringSplitOptions.RemoveEmptyEntries);

            return tokens.Any(token => aliases.Contains(token, StringComparer.Ordinal));
        }
    }
}
