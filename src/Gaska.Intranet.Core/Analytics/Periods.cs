using System.Globalization;

namespace Gaska.Intranet.Core.Analytics;

/// <summary>A span of whole days, both ends included.</summary>
public readonly record struct DayRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    public bool Contains(DateOnly day) => day >= From && day <= To;

    public override string ToString() => $"{From:yyyy-MM-dd}..{To:yyyy-MM-dd}";
}

/// <summary>How a time series is grouped.</summary>
public enum Granularity
{
    Day = 1,
    Week = 2,
    Month = 3,
    Quarter = 4,
    Year = 5,
}

/// <summary>Period of a dashboard chart – relative to today, so a saved chart stays current.</summary>
public enum PeriodPreset
{
    Last7Days = 1,
    Last30Days = 2,
    ThisMonth = 3,
    LastMonth = 4,
    ThisQuarter = 5,
    LastQuarter = 6,
    ThisYear = 7,
    LastYear = 8,
    Last12Months = 9,
    Custom = 100,
}

/// <summary>One group of a time series: its first day and the label shown on the axis.</summary>
public readonly record struct Bucket(DateOnly Start, string Label);

/// <summary>Calendar arithmetic of the charts: presets → dates, "the period before", grouping into buckets.</summary>
public static class Periods
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    public static readonly IReadOnlyList<(PeriodPreset Preset, string Name)> Presets =
    [
        (PeriodPreset.Last7Days, "Ostatnie 7 dni"),
        (PeriodPreset.Last30Days, "Ostatnie 30 dni"),
        (PeriodPreset.ThisMonth, "Ten miesiąc"),
        (PeriodPreset.LastMonth, "Poprzedni miesiąc"),
        (PeriodPreset.ThisQuarter, "Ten kwartał"),
        (PeriodPreset.LastQuarter, "Poprzedni kwartał"),
        (PeriodPreset.ThisYear, "Ten rok"),
        (PeriodPreset.LastYear, "Poprzedni rok"),
        (PeriodPreset.Last12Months, "Ostatnie 12 miesięcy"),
        (PeriodPreset.Custom, "Własny zakres dat"),
    ];

    public static string Name(PeriodPreset preset) => Presets.FirstOrDefault(p => p.Preset == preset).Name ?? preset.ToString();

    /// <summary>Dates of a preset as of <paramref name="today"/>. "This …" presets end today (period to date).</summary>
    public static DayRange Resolve(PeriodPreset preset, DateOnly today, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var quarterStart = new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1);
        var yearStart = new DateOnly(today.Year, 1, 1);

        switch (preset)
        {
            case PeriodPreset.Last7Days:
                return new DayRange(today.AddDays(-6), today);
            case PeriodPreset.Last30Days:
                return new DayRange(today.AddDays(-29), today);
            case PeriodPreset.ThisMonth:
                return new DayRange(monthStart, today);
            case PeriodPreset.LastMonth:
                return new DayRange(monthStart.AddMonths(-1), monthStart.AddDays(-1));
            case PeriodPreset.ThisQuarter:
                return new DayRange(quarterStart, today);
            case PeriodPreset.LastQuarter:
                return new DayRange(quarterStart.AddMonths(-3), quarterStart.AddDays(-1));
            case PeriodPreset.ThisYear:
                return new DayRange(yearStart, today);
            case PeriodPreset.LastYear:
                return new DayRange(yearStart.AddYears(-1), yearStart.AddDays(-1));
            case PeriodPreset.Last12Months:
                // 12 whole calendar months ending with the current one – every bar is a full month except the last.
                return new DayRange(monthStart.AddMonths(-11), today);
            default:
                var from = customFrom ?? today.AddDays(-29);
                var to = customTo ?? today;
                return from <= to ? new DayRange(from, to) : new DayRange(to, from);
        }
    }

    /// <summary>
    /// The period a range is compared with: the same span one month / quarter / year earlier for calendar presets,
    /// otherwise the same number of days immediately before.
    /// </summary>
    public static DayRange Previous(DayRange range, PeriodPreset preset) => preset switch
    {
        PeriodPreset.ThisMonth or PeriodPreset.LastMonth => ShiftMonths(range, -1),
        PeriodPreset.ThisQuarter or PeriodPreset.LastQuarter => ShiftMonths(range, -3),
        PeriodPreset.ThisYear or PeriodPreset.LastYear or PeriodPreset.Last12Months => ShiftMonths(range, -12),
        _ => new DayRange(range.From.AddDays(-range.Days), range.From.AddDays(-1)),
    };

    /// <summary>Moves a range by whole months; an end on the last day of a month stays on the last day.</summary>
    public static DayRange ShiftMonths(DayRange range, int months)
    {
        var to = range.To.AddMonths(months);
        if (range.To.Day == DateTime.DaysInMonth(range.To.Year, range.To.Month))
        {
            to = new DateOnly(to.Year, to.Month, DateTime.DaysInMonth(to.Year, to.Month));
        }

        return new DayRange(range.From.AddMonths(months), to);
    }

    /// <summary>Grouping that keeps a chart readable: days up to a month, weeks up to four months, then months, years.</summary>
    public static Granularity DefaultGranularity(DayRange range) => range.Days switch
    {
        <= 31 => Granularity.Day,
        <= 120 => Granularity.Week,
        <= 366 * 3 => Granularity.Month,
        _ => Granularity.Year,
    };

    public static string Name(Granularity granularity) => granularity switch
    {
        Granularity.Day => "Dni",
        Granularity.Week => "Tygodnie",
        Granularity.Month => "Miesiące",
        Granularity.Quarter => "Kwartały",
        _ => "Lata",
    };

    /// <summary>First day of the bucket a day falls into (weeks start on Monday).</summary>
    public static DateOnly BucketStart(DateOnly day, Granularity granularity) => granularity switch
    {
        Granularity.Day => day,
        Granularity.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        Granularity.Month => new DateOnly(day.Year, day.Month, 1),
        Granularity.Quarter => new DateOnly(day.Year, ((day.Month - 1) / 3 * 3) + 1, 1),
        _ => new DateOnly(day.Year, 1, 1),
    };

    /// <summary>All buckets of a range in order – also the empty ones, so a chart shows gaps as zeros.</summary>
    public static IReadOnlyList<Bucket> Buckets(DayRange range, Granularity granularity)
    {
        var buckets = new List<Bucket>();
        var start = BucketStart(range.From, granularity);
        while (start <= range.To)
        {
            buckets.Add(new Bucket(start, Label(start, granularity)));
            start = granularity switch
            {
                Granularity.Day => start.AddDays(1),
                Granularity.Week => start.AddDays(7),
                Granularity.Month => start.AddMonths(1),
                Granularity.Quarter => start.AddMonths(3),
                _ => start.AddYears(1),
            };
        }

        return buckets;
    }

    public static string Label(DateOnly start, Granularity granularity) => granularity switch
    {
        Granularity.Day => start.ToString("d.MM", Polish),
        Granularity.Week => start.ToString("d.MM", Polish),
        Granularity.Month => $"{MonthShort(start.Month)} {start:yy}",
        Granularity.Quarter => $"Q{((start.Month - 1) / 3) + 1} {start:yy}",
        _ => start.Year.ToString(CultureInfo.InvariantCulture),
    };

    public static string MonthShort(int month) =>
        Polish.DateTimeFormat.AbbreviatedMonthNames[month - 1].TrimEnd('.');

    /// <summary>"1.01.2026 – 13.11.2026"; a whole year is "2026", a whole month "listopad 2026", a whole quarter "Q4 2026".</summary>
    public static string Describe(DayRange range)
    {
        var (from, to) = (range.From, range.To);
        var endsMonth = to.Day == DateTime.DaysInMonth(to.Year, to.Month);
        if (from.Day == 1 && endsMonth && from.Year == to.Year)
        {
            if (from.Month == 1 && to.Month == 12)
            {
                return from.Year.ToString(CultureInfo.InvariantCulture);
            }

            if (from.Month == to.Month)
            {
                return $"{Polish.DateTimeFormat.MonthNames[from.Month - 1]} {from.Year}";
            }

            if ((from.Month - 1) % 3 == 0 && to.Month == from.Month + 2)
            {
                return $"Q{((from.Month - 1) / 3) + 1} {from.Year}";
            }
        }

        return $"{from.ToString("d.MM.yyyy", Polish)} – {to.ToString("d.MM.yyyy", Polish)}";
    }
}
