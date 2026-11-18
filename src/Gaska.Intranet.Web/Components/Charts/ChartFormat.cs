using System.Globalization;
using Gaska.Intranet.Core.Analytics;

namespace Gaska.Intranet.Web.Components.Charts;

/// <summary>One line / group of bars on a chart.</summary>
/// <param name="Slot">
/// Colour slot 1–8 (CSS <c>--gaska-series-N</c>) – fixed for the thing the series stands for, so a period keeps its
/// colour on every chart of the page. 0 = the muted "previous period".
/// </param>
public sealed record ChartSeries(string Name, IReadOnlyList<decimal> Values, int Slot = 1);

/// <summary>Numbers as shown on charts and in tables (Polish formatting).</summary>
public static class ChartFormat
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>The exact value: "1 234 567,89 zł" / "1 234".</summary>
    public static string Full(decimal value, MetricUnit unit) => unit switch
    {
        MetricUnit.Money => value.ToString("N2", Polish) + " zł",
        MetricUnit.Days => value.ToString("N0", Polish) + (value == 1 ? " dzień" : " dni"),
        _ => value.ToString("N0", Polish),
    };

    /// <summary>Short value for axes and headline numbers: "1,2 mln zł", "350 tys.", "42".</summary>
    public static string Compact(decimal value, MetricUnit unit)
    {
        var abs = Math.Abs(value);
        var text = abs switch
        {
            >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.##", Polish) + " mld",
            >= 1_000_000 => (value / 1_000_000).ToString("0.##", Polish) + " mln",
            >= 10_000 => (value / 1_000).ToString("0.#", Polish) + " tys.",
            _ => value.ToString(unit == MetricUnit.Money && abs < 100 ? "0.##" : "N0", Polish),
        };

        return unit switch
        {
            MetricUnit.Money => text + " zł",
            MetricUnit.Days => text + (value == 1 ? " dzień" : " dni"),
            _ => text,
        };
    }

    /// <summary>Share of a whole: "7%", "0,4%"; below a tenth of a percent "&lt;0,1%".</summary>
    public static string Share(decimal part, decimal whole)
    {
        if (whole <= 0)
        {
            return "—";
        }

        var share = part / whole;
        return share is > 0 and < 0.001m ? "<0,1%" : share.ToString(share < 0.1m ? "0.#%" : "0%", Polish).Replace(" ", string.Empty);
    }

    /// <summary>Relative change, "+12,4%" / "−3,0%"; null when there is nothing to compare with.</summary>
    public static string? Change(decimal current, decimal previous)
    {
        if (previous == 0)
        {
            return null;
        }

        var change = (current - previous) / Math.Abs(previous);
        return (change >= 0 ? "+" : "−") + Math.Abs(change).ToString("0.0%", Polish).Replace(" ", string.Empty);
    }

    /// <summary>Value for inline CSS (always with a dot).</summary>
    public static string Css(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>A value axis with round tick values that always includes zero.</summary>
public sealed class ChartScale
{
    /// <param name="wholeNumbers">Counts: ticks are whole numbers (no "0,5 complaints").</param>
    public ChartScale(IEnumerable<decimal> values, int targetTicks = 4, bool wholeNumbers = false)
    {
        var all = values.DefaultIfEmpty(0m).ToList();
        var max = (double)Math.Max(all.Max(), 0m);
        var min = (double)Math.Min(all.Min(), 0m);
        if (max - min <= 0)
        {
            max = 1;
        }

        var step = NiceStep((max - min) / targetTicks);
        if (wholeNumbers)
        {
            step = Math.Max(1, step);
        }

        Min = Math.Floor(min / step) * step;
        Max = Math.Ceiling(max / step) * step;

        var ticks = new List<decimal>();
        for (var tick = Min; tick <= Max + (step / 2); tick += step)
        {
            ticks.Add((decimal)Math.Round(tick, 6));
        }

        Ticks = ticks;
    }

    public double Min { get; }

    public double Max { get; }

    public IReadOnlyList<decimal> Ticks { get; }

    /// <summary>Position of a value on the axis, 0 (bottom) – 100 (top).</summary>
    public double Percent(decimal value) => ((double)value - Min) / (Max - Min) * 100;

    private static double NiceStep(double rough)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var fraction = rough / magnitude;
        return magnitude * (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 2.5 ? 2.5 : fraction <= 5 ? 5 : 10);
    }
}
