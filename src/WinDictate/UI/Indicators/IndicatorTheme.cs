using Microsoft.Win32;
using System.Windows;
using Color = System.Windows.Media.Color;

namespace WinDictate.UI.Indicators;

/// <summary>Every colour an indicator draws with, for one Windows appearance.</summary>
internal sealed record IndicatorPalette(
    Color Fill,        // the glass itself
    Color Edge,        // hairline around the glass
    Color Ink,         // waveform, dots, words
    Color Recording,   // the small dot that means "the microphone is on"
    Color Success,
    Color Warning,
    Color Info,
    Color Glow,        // edge glow body
    Color GlowCore,    // edge glow highlight
    Color Shadow,
    double ShadowOpacity)
{
    /// <summary>Windows light mode: milky white glass, dark ink.</summary>
    public static readonly IndicatorPalette Light = new(
        Fill: Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF),
        Edge: Color.FromArgb(0x24, 0x18, 0x26, 0x3C),
        Ink: Color.FromRgb(0x22, 0x29, 0x32),
        Recording: Color.FromRgb(0xE5, 0x48, 0x4D),
        Success: Color.FromRgb(0x2E, 0x9E, 0x6A),
        Warning: Color.FromRgb(0xD9, 0x8E, 0x1F),
        Info: Color.FromRgb(0x2F, 0x7B, 0xEA),
        Glow: Color.FromRgb(0x2F, 0x7B, 0xEA),
        GlowCore: Color.FromRgb(0xDC, 0xEB, 0xFF),
        Shadow: Color.FromRgb(0x18, 0x26, 0x3C),
        ShadowOpacity: 0.20);

    /// <summary>Windows dark mode: smoked glass, light ink.</summary>
    public static readonly IndicatorPalette Dark = new(
        Fill: Color.FromArgb(0xF7, 0x2C, 0x30, 0x38),
        Edge: Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
        Ink: Color.FromRgb(0xF2, 0xF5, 0xF9),
        Recording: Color.FromRgb(0xF0, 0x64, 0x6A),
        Success: Color.FromRgb(0x5C, 0xC2, 0x8C),
        Warning: Color.FromRgb(0xE0, 0xA8, 0x4A),
        Info: Color.FromRgb(0x6F, 0xAE, 0xFF),
        Glow: Color.FromRgb(0x6F, 0xAE, 0xFF),
        GlowCore: Color.FromRgb(0xFF, 0xFF, 0xFF),
        Shadow: Color.FromRgb(0x00, 0x00, 0x00),
        ShadowOpacity: 0.45);
}

/// <summary>Reads the Windows appearance the indicators should follow.</summary>
internal static class IndicatorTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Test hook: when set, <see cref="Current"/> returns this palette regardless of
    /// the Windows appearance, so both looks can be checked without changing the
    /// machine's own settings. Never set in the app itself.
    /// </summary>
    internal static IndicatorPalette? Forced { get; set; }

    /// <summary>The palette for the current Windows appearance. Cheap enough to call on every show.</summary>
    public static IndicatorPalette Current()
    {
        if (Forced is { } forced) return forced;
        if (SystemParameters.HighContrast) return HighContrast();
        return AppsUseDarkTheme() ? IndicatorPalette.Dark : IndicatorPalette.Light;
    }

    /// <summary>False when the user has turned off animations in Windows (Accessibility, Visual effects).</summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    private static bool AppsUseDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>High contrast: opaque system colours only, so nothing depends on translucency or hue.</summary>
    private static IndicatorPalette HighContrast()
    {
        Color window = System.Windows.SystemColors.WindowColor;
        Color text = System.Windows.SystemColors.WindowTextColor;
        Color highlight = System.Windows.SystemColors.HighlightColor;
        return new IndicatorPalette(
            Fill: window,
            Edge: text,
            Ink: text,
            Recording: highlight,
            Success: text,
            Warning: text,
            Info: text,
            Glow: highlight,
            GlowCore: text,
            Shadow: window,
            ShadowOpacity: 0);
    }
}
