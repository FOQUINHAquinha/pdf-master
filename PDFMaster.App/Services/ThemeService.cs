using System.Windows;
using System.Windows.Media;
using PDFMaster.Domain;

namespace PDFMaster.App.Services;

/// <summary>Aplica o tema claro/escuro e a cor de destaque em tempo de execução.</summary>
public sealed class ThemeService
{
    public static readonly IReadOnlyList<string> AccentPresets =
    [
        "#008B50", "#0F7B6C", "#0067C0", "#5B5FC7", "#8E44AD", "#C2410C", "#B4233A", "#4B5563",
    ];

    private ResourceDictionary? _current;

    public void Apply(AppTheme theme, string? accentHex)
    {
        var app = Application.Current;
        var dark = theme == AppTheme.Dark;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"/PDFMaster;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };

        // Tema Fluent nativo do WPF para os controles padrão (botões, caixas de texto, listas).
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        if (_current is not null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Add(dictionary);
        _current = dictionary;

        var accent = TryParse(accentHex) ?? (Color)dictionary["Color.Accent"];
        var window = (Color)dictionary["Color.Window"];
        var onAccent = Luminance(accent) > 0.5 ? Color.FromRgb(0x10, 0x14, 0x12) : Colors.White;

        Set(app, "Brush.Accent", accent);
        Set(app, "Brush.AccentHover", Blend(accent, dark ? Colors.White : Colors.Black, 0.88));
        Set(app, "Brush.AccentSoft", Blend(accent, window, dark ? 0.22 : 0.14));
        Set(app, "Brush.OnAccent", onAccent);

        // Chaves de destaque usadas pelos modelos do tema Fluent.
        Set(app, "AccentFillColorDefaultBrush", accent);
        Set(app, "AccentFillColorSecondaryBrush", Blend(accent, window, 0.9));
        Set(app, "AccentFillColorTertiaryBrush", Blend(accent, window, 0.8));
        Set(app, "AccentTextFillColorPrimaryBrush", accent);
        Set(app, "TextOnAccentFillColorPrimaryBrush", onAccent);
    }

    public static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Set(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    /// <summary>Mistura <paramref name="a"/> sobre <paramref name="b"/>; amount = peso de <paramref name="a"/>.</summary>
    private static Color Blend(Color a, Color b, double amount)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x * amount + y * (1 - amount));
        return Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
