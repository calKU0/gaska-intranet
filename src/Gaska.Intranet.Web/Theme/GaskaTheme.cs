using MudBlazor;

namespace Gaska.Intranet.Web.Theme;

/// <summary>Gąska brand colors (from the logo): yellow #F9C211 and graphite #646363.</summary>
public static class GaskaTheme
{
    /// <summary>Culture for MudBlazor components that default to the invariant one (e.g. "40 675" in grid pagers).</summary>
    public static readonly System.Globalization.CultureInfo Polish = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");

    public const string Yellow = "#F9C211";
    public const string YellowDark = "#E0A800";
    public const string Graphite = "#646363";
    public const string Charcoal = "#2E2E2E";

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Yellow,
            PrimaryDarken = YellowDark,
            PrimaryContrastText = "#1F1F1F",
            Secondary = Graphite,
            SecondaryContrastText = "#FFFFFF",
            Tertiary = Charcoal,
            Info = "#2F80ED",
            Success = "#2E9D5B",
            Warning = "#F2994A",
            Error = "#D64545",
            Background = "#F4F4F3",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = Charcoal,
            DrawerBackground = Charcoal,
            DrawerText = "#E8E8E8",
            DrawerIcon = "#B5B5B5",
            TextPrimary = "#262626",
            TextSecondary = "#6B6B6B",
            ActionDefault = Graphite,
            LinesDefault = "#E4E4E2",
            TableLines = "#ECECEA",
            TableHover = "#FFF8E1",
            Divider = "#E4E4E2",
        },
        // Same brand yellow; graphite surfaces instead of pure black (easier on the eyes, cards still stand out).
        // Keep in sync with the html.gaska-dark variables in app.css.
        PaletteDark = new PaletteDark
        {
            Primary = Yellow,
            PrimaryDarken = YellowDark,
            PrimaryContrastText = "#1F1F1F",
            Secondary = "#B4B4B2",
            SecondaryContrastText = "#1F1F1F",
            Tertiary = "#D0D0CE",
            Info = "#5B9BF0",
            Success = "#4CB873",
            Warning = "#F2A65A",
            Error = "#EB6A6A",
            Background = "#161616",
            BackgroundGray = "#1C1C1C",
            Surface = "#1F1F1F",
            AppbarBackground = "#1F1F1F",
            AppbarText = "#E8E8E6",
            DrawerBackground = "#121212",
            DrawerText = "#E8E8E8",
            DrawerIcon = "#B5B5B5",
            TextPrimary = "#E8E8E6",
            TextSecondary = "#A5A5A3",
            TextDisabled = "#6E6E6C",
            ActionDefault = "#B4B4B2",
            ActionDisabled = "#5A5A58",
            ActionDisabledBackground = "#2A2A29",
            LinesDefault = "#333332",
            LinesInputs = "#4A4A48",
            TableLines = "#2C2C2B",
            TableHover = "rgba(249, 194, 17, .08)",
            Divider = "#333332",
            OverlayLight = "rgba(22, 22, 22, .6)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"],
            },
            // Sentence case instead of CAPITALS: easier to read and ~25% narrower – labels like "Zapisz w GoNet"
            // no longer wrap onto two lines on phones.
            Button = new ButtonTypography
            {
                TextTransform = "none",
                FontWeight = "600",
                LetterSpacing = "0",
            },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            AppbarHeight = "64px",
            DrawerWidthLeft = "248px",
        },
    };
}
