using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace SvuSessionsLauncher.Theming;

/// <summary>Light or dark appearance.</summary>
public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// A selectable accent colour. <see cref="Swatch"/> is a frozen brush suitable
/// for binding a colour chip in the appearance popup.
/// </summary>
public sealed record Accent(string Key, string Label, Color Primary, Color Hover, Color Pressed)
{
    private static readonly Dictionary<Color, SolidColorBrush> SwatchCache = [];

    /// <summary>
    /// Frozen brush for the colour chip. Deliberately a computed property with no
    /// backing field: the <see cref="Accent"/> record's generated equality/hash
    /// therefore never changes while it is live. A stored field would mutate the
    /// hash the moment the chip first renders, corrupting the ListBox's selection
    /// lookup (the accent would silently snap back to the first entry).
    /// </summary>
    public SolidColorBrush Swatch
    {
        get
        {
            lock (SwatchCache)
            {
                if (!SwatchCache.TryGetValue(Primary, out var brush))
                {
                    brush = new SolidColorBrush(Primary);
                    brush.Freeze();
                    SwatchCache[Primary] = brush;
                }

                return brush;
            }
        }
    }
}

/// <summary>
/// A named choice for the header "radiant" strip. The three gradient stops are
/// derived from <see cref="Primary"/> (lighter → primary → darker) so any colour
/// always produces a pleasing shine.
/// </summary>
public sealed record HeaderChoice(string Key, string Label, Color Primary);

/// <summary>
/// A whole-look preset: theme + accent + header radiant in one pick, so users get
/// a great result without fiddling with each control.
/// </summary>
public sealed record AppearancePreset(string Key, string Label, AppTheme Theme, string Accent, string Header);

/// <summary>
/// Runtime theming. The default palette lives in <c>Themes\Theme.xaml</c>, but
/// WPF freezes those brushes once the dictionary is merged into the application
/// resources, so they cannot be recoloured in place. Instead every palette key
/// is <em>replaced</em> at the application-resources level with a fresh
/// (unfrozen) brush. All consumers reference the keys with
/// <c>DynamicResource</c>, so re-assigning a key repaints every window, list,
/// chip and popup instantly — no rebuild, reload or re-fetch. Gradients are
/// replaced the same way, stop by stop. The app font is swapped through the
/// <c>Font.App</c> key, which styles also consume via <c>DynamicResource</c>.
/// </summary>
public static class ThemeManager
{
    /// <summary>The palette currently applied. Seed value is <see cref="AppTheme.Light"/>.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>Raised after <see cref="Apply"/> swaps the palette, so surfaces that
    /// live outside the resource system (e.g. a window's native title bar) can
    /// re-tint themselves.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<Accent> Accents { get; } = new[]
    {
        Make("teal", "Teal", 0x0F766E),
        Make("emerald", "Emerald", 0x059669),
        Make("green", "Green", 0x16A34A),
        Make("sky", "Sky", 0x0284C7),
        Make("cyan", "Cyan", 0x0891B2),
        Make("blue", "Blue", 0x2563EB),
        Make("indigo", "Indigo", 0x4F46E5),
        Make("violet", "Violet", 0x7C3AED),
        Make("purple", "Purple", 0x9333EA),
        Make("fuchsia", "Fuchsia", 0xC026D3),
        Make("pink", "Pink", 0xDB2777),
        Make("rose", "Rose", 0xE11D48),
        Make("orange", "Orange", 0xEA580C),
        Make("amber", "Amber", 0xD97706),
        Make("slate", "Slate", 0x475569),
        Make("gold", "Gold", 0xCA8A04),
        Make("lime", "Lime", 0x65A30D),
        Make("mint", "Mint", 0x14B8A6),
        Make("ocean", "Ocean", 0x0B7285),
        Make("denim", "Denim", 0x3B5BDB),
        Make("crimson", "Crimson", 0xBE123C),
        Make("magenta", "Magenta", 0xD6249F),
        Make("terracotta", "Terracotta", 0xC2410C),
        Make("wine", "Wine", 0x881337),
        Make("steel", "Steel", 0x64748B),
    };

    private static Accent Make(string key, string label, int rgb) => Make(key, label, H(rgb));

    private static Accent Make(string key, string label, Color primary) =>
        new(key, label, primary, Blend(primary, Colors.White, 0.18), Blend(primary, Colors.Black, 0.22));

    /// <summary>Accent key for a colour picked by the user instead of a named palette entry.</summary>
    public const string CustomAccent = "custom";

    /// <summary>Header radiant key meaning "follow the chosen accent".</summary>
    public const string AutoHeader = "auto";

    /// <summary>Header "radiant" colour choices (3 gradient stops are derived from each).</summary>
    public static IReadOnlyList<HeaderChoice> HeaderChoices { get; } = new[]
    {
        new HeaderChoice("ocean", "Ocean", H(0x0284C7)),
        new HeaderChoice("sky", "Sky", H(0x0EA5E9)),
        new HeaderChoice("sunset", "Sunset", H(0xEA580C)),
        new HeaderChoice("amber", "Amber", H(0xD97706)),
        new HeaderChoice("forest", "Forest", H(0x16A34A)),
        new HeaderChoice("grape", "Grape", H(0x7C3AED)),
        new HeaderChoice("rose", "Rose", H(0xE11D48)),
        new HeaderChoice("graphite", "Graphite", H(0x475569)),
        new HeaderChoice("ink", "Ink", H(0x1E293B)),
    };

    /// <summary>Ready-made whole-look presets shown at the top of the appearance popup.</summary>
    public static IReadOnlyList<AppearancePreset> Presets { get; } = new[]
    {
        new AppearancePreset("teal-light", "Teal", AppTheme.Light, "teal", AutoHeader),
        new AppearancePreset("emerald-light", "Emerald", AppTheme.Light, "emerald", "forest"),
        new AppearancePreset("sky-light", "Sky", AppTheme.Light, "sky", "ocean"),
        new AppearancePreset("rose-light", "Rose", AppTheme.Light, "rose", AutoHeader),
        new AppearancePreset("grape-dark", "Grape", AppTheme.Dark, "purple", "grape"),
        new AppearancePreset("midnight", "Midnight", AppTheme.Dark, "indigo", "ink"),
        new AppearancePreset("sky-dark", "Sky Dark", AppTheme.Dark, "sky", "ocean"),
        new AppearancePreset("graphite-light", "Graphite", AppTheme.Light, "slate", "graphite"),
        new AppearancePreset("sunset-light", "Sunset", AppTheme.Light, "orange", "sunset"),
        new AppearancePreset("ocean-dark", "Ocean", AppTheme.Dark, "cyan", "ocean"),
        new AppearancePreset("gold-light", "Gold", AppTheme.Light, "gold", "amber"),
        new AppearancePreset("mint-light", "Mint", AppTheme.Light, "mint", AutoHeader),
        new AppearancePreset("terracotta-light", "Terracotta", AppTheme.Light, "terracotta", "sunset"),
        new AppearancePreset("crimson-dark", "Crimson", AppTheme.Dark, "crimson", "rose"),
        new AppearancePreset("wine-dark", "Wine", AppTheme.Dark, "wine", AutoHeader),
        new AppearancePreset("steel-dark", "Steel", AppTheme.Dark, "steel", "graphite"),
        new AppearancePreset("blue-light", "Blue", AppTheme.Light, "blue", "ocean"),
        new AppearancePreset("indigo-light", "Indigo", AppTheme.Light, "indigo", "grape"),
        new AppearancePreset("violet-dark", "Violet", AppTheme.Dark, "violet", AutoHeader),
        new AppearancePreset("pink-light", "Pink", AppTheme.Light, "pink", "rose"),
        new AppearancePreset("amber-light", "Amber", AppTheme.Light, "amber", "amber"),
        new AppearancePreset("lime-light", "Lime", AppTheme.Light, "lime", "forest"),
        new AppearancePreset("denim-dark", "Denim", AppTheme.Dark, "denim", "ocean"),
        new AppearancePreset("magenta-dark", "Magenta", AppTheme.Dark, "magenta", "grape"),
        new AppearancePreset("mint-dark", "Mint Dark", AppTheme.Dark, "mint", AutoHeader),
        new AppearancePreset("steel-light", "Steel Light", AppTheme.Light, "steel", "graphite"),
        new AppearancePreset("ocean-light", "Ocean Light", AppTheme.Light, "ocean", "ocean"),
    };

    /// <summary>
    /// Resolves the three header (and hero) gradient stops for an accent + header
    /// key. <see cref="AutoHeader"/> derives the stops from the accent; any other
    /// key derives them from the named colour.
    /// </summary>
    public static Color[] ResolveHeaderStops(string? accentKey, string? headerKey, string? accentHex = null) =>
        ResolveHeaderStops(Resolve(accentKey, accentHex), headerKey);

    /// <summary>
    /// Resolves the header stops from an already resolved accent, so a custom
    /// accent's "auto" header follows the picked colour instead of the default.
    /// </summary>
    public static Color[] ResolveHeaderStops(Accent accent, string? headerKey)
    {
        if (string.IsNullOrWhiteSpace(headerKey)
            || string.Equals(headerKey, AutoHeader, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { accent.Hover, accent.Primary, accent.Pressed };
        }

        var choice = HeaderChoices.FirstOrDefault(h => string.Equals(h.Key, headerKey, StringComparison.OrdinalIgnoreCase))
                     ?? HeaderChoices[0];
        return new[] { Blend(choice.Primary, Colors.White, 0.18), choice.Primary, Blend(choice.Primary, Colors.Black, 0.22) };
    }

    /// <summary>Builds a frozen three-stop corner gradient, e.g. for a swatch chip.</summary>
    public static LinearGradientBrush Gradient(Color a, Color b, Color c)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(a, 0));
        brush.GradientStops.Add(new GradientStop(b, 0.55));
        brush.GradientStops.Add(new GradientStop(c, 1));
        brush.Freeze();
        return brush;
    }

    public static AppTheme Parse(string? value) =>
        string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark : AppTheme.Light;

    public static string Key(AppTheme theme) => theme == AppTheme.Dark ? "dark" : "light";

    public static Accent Resolve(string? accentKey, string? accentHex = null)
    {
        if (string.Equals(accentKey, CustomAccent, StringComparison.OrdinalIgnoreCase)
            && TryParseColor(accentHex, out var custom))
        {
            return Make(CustomAccent, "Custom", custom);
        }

        return Accents.FirstOrDefault(a => string.Equals(a.Key, accentKey, StringComparison.OrdinalIgnoreCase)) ?? Accents[0];
    }

    /// <summary>Parses a user-supplied "#RRGGBB" (or "RRGGBB") colour.</summary>
    public static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        var text = value?.Trim().TrimStart('#');
        if (text is not { Length: 6 }
            || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = H((int)rgb);
        return true;
    }

    /// <summary>Formats a colour as "#RRGGBB" for the HTML views.</summary>
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Readable text/icon colour on the accent surface. "auto" picks whichever
    /// of white or near-black has the better contrast; "black"/"white" force it.
    /// </summary>
    public static Color ResolveOnPrimary(Accent accent, string? inkKey = null) =>
        inkKey?.ToLowerInvariant() switch
        {
            "black" => Colors.Black,
            "white" => Colors.White,
            _ => Contrast(Colors.White, accent.Primary) >= Contrast(H(0x0A0E15), accent.Primary)
                ? Colors.White
                : H(0x0A0E15),
        };

    public static Color ResolveOnPrimary(string? accentKey, string? inkKey = null, string? accentHex = null) =>
        ResolveOnPrimary(Resolve(accentKey, accentHex), inkKey);

    /// <summary>Selected-row tint derived from the accent for the active theme.</summary>
    public static Color Selection(Accent accent, AppTheme theme) =>
        theme == AppTheme.Dark
            ? Blend(accent.Primary, H(0x0A0E15), 0.76)
            : Blend(accent.Primary, H(0xFFFFFF), 0.88);

    /// <summary>Low-alpha accent wash used by soft surfaces.</summary>
    public static Color PrimarySoft(Accent accent, AppTheme theme) =>
        WithAlpha(accent.Primary, theme == AppTheme.Dark ? (byte)0x30 : (byte)0x1F);

    /// <summary>Quiet accent edge for floating cards.</summary>
    public static Color AccentEdge(Accent accent, AppTheme theme) =>
        WithAlpha(accent.Primary, theme == AppTheme.Dark ? (byte)0x5C : (byte)0x3A);

    /// <summary>Applies the palette for the given theme + accent + header radiant in place.</summary>
    public static void Apply(
        AppTheme theme,
        string? accentKey,
        string? headerKey = null,
        string? inkKey = null,
        string? accentHex = null)
    {
        var accent = Resolve(accentKey, accentHex);
        var dark = theme == AppTheme.Dark;
        Current = theme;

        Set("Brush.Background", dark ? H(0x0A0E15) : H(0xF2F5FA));
        // The chat page's own --bg (Assets\chat\app.css), so the window around
        // the embedded session matches the loader instead of flashing another
        // shade while the WebView initializes.
        Set("Brush.ChatBackground", dark ? H(0x0B1216) : H(0xEEF1F4));
        Set("Brush.Surface", dark ? H(0x111823) : H(0xFFFFFF));
        Set("Brush.SurfaceAlt", dark ? H(0x17202E) : H(0xF6F8FC));
        Set("Brush.Border", dark ? H(0x212C3D) : H(0xE2E8F2));
        Set("Brush.BorderStrong", dark ? H(0x2F3D53) : H(0xCBD6E4));

        Set("Brush.ScrollThumb", dark ? H(0x2A3648) : H(0xC9D3E2));
        Set("Brush.ScrollThumbHover", dark ? H(0x3C4C63) : H(0x9AA9BD));
        Set("Brush.Text", dark ? H(0xEAF0F8) : H(0x0D1520));
        Set("Brush.Muted", dark ? H(0x8A99B0) : H(0x5E6E85));

        Set("Brush.Primary", accent.Primary);
        Set("Brush.PrimaryHover", accent.Hover);
        Set("Brush.PrimaryPressed", accent.Pressed);

        // Readable text/icon colour on top of the accent, unless the user
        // explicitly asked for black or white ink.
        Set("Brush.OnPrimary", ResolveOnPrimary(accent, inkKey));

        Set("Brush.Selection", Selection(accent, theme));

        Set("Brush.PrimarySoft", PrimarySoft(accent, theme));

        // A visible but quiet accent edge for the floating cards, so the tint
        // reads as warmth around the boxes instead of only inside them.
        Set("Brush.AccentEdge", AccentEdge(accent, theme));

        // Soft status chips: dark theme uses deep tinted fills with light text.
        Set("Brush.StatusAvailableBg", dark ? H(0x12331F) : H(0xDCFCE7));
        Set("Brush.StatusProgressBg", dark ? H(0x3A2A08) : H(0xFEF3C7));
        Set("Brush.StatusFinishedBg", dark ? H(0x1E2A44) : H(0xE2E8F0));
        Set("Brush.StatusCancelledBg", dark ? H(0x3A1517) : H(0xFEE2E2));
        Set("Brush.StatusAvailableFg", dark ? H(0x86EFAC) : H(0x15803D));
        Set("Brush.StatusProgressFg", dark ? H(0xFCD34D) : H(0xB45309));
        Set("Brush.StatusFinishedFg", dark ? H(0xCBD5E1) : H(0x475569));
        Set("Brush.StatusCancelledFg", dark ? H(0xFCA5A5) : H(0xB91C1C));

        Set("Brush.Danger", H(0xDC2626));
        Set("Brush.DangerHover", H(0xEF4444));

        var headerStops = ResolveHeaderStops(accent, headerKey);
        SetGradient("Brush.HeroGradient", headerStops);

        Changed?.Invoke();
    }

    /// <summary>Swaps the app font: bundled Cairo for Arabic, Rubik for English.</summary>
    public static void ApplyFont(string? language)
    {
        var arabic = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase);
        var fontDirectory = Path.TrimEndingDirectorySeparator(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"));
        var fontUri = new Uri(fontDirectory + Path.DirectorySeparatorChar);

        FontFamily CreateFamily(string familyName, string fallback)
        {
            try
            {
                return new FontFamily(fontUri, "./#" + familyName);
            }
            catch
            {
                return new FontFamily(fallback);
            }
        }

        var english = CreateFamily("Rubik", "Rubik, Segoe UI, Tahoma, Arial");
        var arabicFamily = CreateFamily("Cairo", "Cairo, Dubai, Segoe UI, Tahoma, Arial");

        if (Application.Current is { } app)
        {
            app.Resources["Font.English"] = english;
            app.Resources["Font.Arabic"] = arabicFamily;
            app.Resources["Font.App"] = arabic ? arabicFamily : english;
        }
    }

    private static void Set(string key, Color color)
    {
        // The merged-dictionary brush is frozen, so publishing a brand new
        // (unfrozen) brush at the application level is the only way to change
        // it. DynamicResource consumers re-resolve when the key changes. The
        // replacement is frozen too: a frozen brush is cheaper to render and,
        // because each Apply publishes a whole new brush, there is nothing to
        // mutate in place.
        if (Application.Current is { } app)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }

    private static void SetGradient(string key, params Color[] stops)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var clone = app.Resources[key] is LinearGradientBrush existing
            ? (LinearGradientBrush)existing.Clone()
            : new LinearGradientBrush();

        ApplyStops(clone, stops);
        clone.Freeze();
        app.Resources[key] = clone;
    }

    private static void ApplyStops(LinearGradientBrush brush, Color[] stops)
    {
        while (brush.GradientStops.Count < stops.Length)
        {
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        }

        for (var i = 0; i < stops.Length && i < brush.GradientStops.Count; i++)
        {
            brush.GradientStops[i].Color = stops[i];
        }
    }

    private static Color H(int rgb) =>
        Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    private static Color Blend(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x * (1 - t) + y * t);
        return Color.FromArgb(255, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    /// <summary>WCAG relative luminance of an sRGB colour.</summary>
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.03928
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG contrast ratio between two opaque colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
