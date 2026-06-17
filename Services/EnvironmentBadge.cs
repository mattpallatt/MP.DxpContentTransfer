using System.Globalization;

namespace DxpContentTransfer.Services;

// Presentation helpers for the top-bar environment badge: the default per-environment colour, the
// display label, the default placement selector, and the accessibility-aware text colour. Shared by
// the script-serving controller, the settings controller, and the settings view's live update.
public static class EnvironmentBadge
{
    // The CSS selector for the top-bar label the badge is placed next to. Overridable from settings
    // (advanced) in case a CMS update moves it; this is the built-in fallback.
    public const string DefaultSelector = ".epi-pn-navigation__section--align-center .flex--1.truncate";

    private const string DarkText = "#1a1a1a";
    private const string LightText = "#ffffff";
    // WCAG relative-luminance crossover where black and white text have equal contrast (L = 0.179).
    private const double LuminanceThreshold = 0.179;

    // Integration/Preproduction (and a local Development fallback) get a colour; Production's default
    // is red. Used when an environment has no custom colour set.
    public static string DefaultColor(string name) => name?.ToLowerInvariant() switch
    {
        "integration" => "#d4651a",
        "preproduction" => "#7b2fff",
        "production" => "#c0392b",
        "development" => "#2e7d32",
        _ => null,
    };

    // The pill text: the admin's custom label as typed, or the upper-cased environment name when no
    // override is set. Display only — never used for matching.
    public static string EffectiveLabel(string name, string customLabel) =>
        string.IsNullOrWhiteSpace(customLabel) ? name?.ToUpperInvariant() : customLabel.Trim();

    // The more accessible text colour for a background hex (#rgb or #rrggbb): near-black on light
    // backgrounds, white on dark ones.
    public static string TextColor(string hexBackground)
    {
        if (!TryParse(hexBackground, out var r, out var g, out var b))
            return LightText;

        var luminance = 0.2126 * Linearise(r) + 0.7152 * Linearise(g) + 0.0722 * Linearise(b);
        return luminance > LuminanceThreshold ? DarkText : LightText;
    }

    private static double Linearise(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static bool TryParse(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6) return false;

        return int.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
             & int.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
             & int.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
