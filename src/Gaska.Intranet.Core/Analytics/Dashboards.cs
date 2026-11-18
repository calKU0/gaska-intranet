namespace Gaska.Intranet.Core.Analytics;

/// <summary>How a dashboard chart draws its metric.</summary>
public enum ChartKind
{
    Bars = 1,
    Line = 2,

    /// <summary>One headline number with the change against the previous period.</summary>
    Number = 3,
    Table = 4,

    /// <summary>Shares of a whole (rankings only): the top rows and "the rest".</summary>
    Donut = 5,
}

/// <summary>What a dashboard item is.</summary>
public enum WidgetKind
{
    Chart = 0,

    /// <summary>A heading that starts a group of charts (title and an optional description) – moved around like a chart.</summary>
    Heading = 1,
}

/// <summary>Chart width on a desktop: a quarter, a third, a half or the full width of the page.</summary>
public enum WidgetWidth
{
    Small = 1,
    Medium = 2,
    Full = 3,

    /// <summary>A quarter of the page – four headline numbers in a row.</summary>
    Quarter = 4,
}

/// <summary>A user's own page of charts ("Sprzedaż", "Komunikacja z klientami").</summary>
public class Dashboard
{
    public int Id { get; set; }

    /// <summary>SID of the owner – dashboards are private.</summary>
    public string OwnerSid { get; set; } = string.Empty;

    public string OwnerAccount { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<DashboardWidget> Widgets { get; set; } = [];
}

/// <summary>One chart on a dashboard.</summary>
public class DashboardWidget
{
    public int Id { get; set; }
    public int DashboardId { get; set; }

    public WidgetKind Kind { get; set; }

    /// <summary><see cref="MetricDefinition.Key"/> (empty for a heading).</summary>
    public string MetricKey { get; set; } = string.Empty;

    /// <summary>Own title; null = the metric's title. The text of a heading.</summary>
    public string? Title { get; set; }

    /// <summary>The line under a heading.</summary>
    public string? Description { get; set; }

    /// <summary>Colour slot 1–8 of the chart's marks; 0 = the default colour.</summary>
    public int Color { get; set; }

    public ChartKind Chart { get; set; } = ChartKind.Bars;
    public PeriodPreset Period { get; set; } = PeriodPreset.Last12Months;
    public DateOnly? CustomFrom { get; set; }
    public DateOnly? CustomTo { get; set; }

    /// <summary>Null = chosen from the length of the period.</summary>
    public Granularity? Granularity { get; set; }

    public WidgetWidth Width { get; set; } = WidgetWidth.Medium;

    /// <summary>Also draw the previous period (series charts).</summary>
    public bool ComparePrevious { get; set; }

    /// <summary>Number of rows of a ranking.</summary>
    public int Top { get; set; } = 10;

    public int SortOrder { get; set; }

    public WidgetInput ToInput() => new()
    {
        Kind = Kind,
        MetricKey = MetricKey,
        Title = Title,
        Description = Description,
        Color = Color,
        Chart = Chart,
        Period = Period,
        CustomFrom = CustomFrom,
        CustomTo = CustomTo,
        Granularity = Granularity,
        Width = Width,
        ComparePrevious = ComparePrevious,
        Top = Top,
    };
}

/// <summary>Chart (or heading) settings chosen by the user.</summary>
public sealed record WidgetInput
{
    public WidgetKind Kind { get; init; }
    public string MetricKey { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Description { get; init; }
    public int Color { get; init; }
    public ChartKind Chart { get; init; } = ChartKind.Bars;
    public PeriodPreset Period { get; init; } = PeriodPreset.Last12Months;
    public DateOnly? CustomFrom { get; init; }
    public DateOnly? CustomTo { get; init; }
    public Granularity? Granularity { get; init; }
    public WidgetWidth Width { get; init; } = WidgetWidth.Medium;
    public bool ComparePrevious { get; init; }
    public int Top { get; init; } = 10;
}

/// <summary>A user's dashboards. Every call is limited to the owner – nobody reads or changes someone else's pages.</summary>
public interface IDashboardService
{
    const int MaxDashboards = 20;
    const int MaxWidgets = 40;

    /// <summary>The owner's dashboards with their charts, in display order.</summary>
    Task<IReadOnlyList<Dashboard>> GetAsync(string ownerSid, CancellationToken ct = default);

    Task<Dashboard> CreateAsync(
        string ownerSid, string ownerAccount, string name, IReadOnlyList<WidgetInput>? widgets = null, CancellationToken ct = default);

    Task RenameAsync(string ownerSid, int dashboardId, string name, CancellationToken ct = default);

    Task DeleteAsync(string ownerSid, int dashboardId, CancellationToken ct = default);

    Task<DashboardWidget> AddWidgetAsync(string ownerSid, int dashboardId, WidgetInput input, CancellationToken ct = default);

    Task UpdateWidgetAsync(string ownerSid, int widgetId, WidgetInput input, CancellationToken ct = default);

    Task RemoveWidgetAsync(string ownerSid, int widgetId, CancellationToken ct = default);

    /// <summary>Moves the chart one place earlier (<paramref name="direction"/> &lt; 0) or later.</summary>
    Task MoveWidgetAsync(string ownerSid, int widgetId, int direction, CancellationToken ct = default);

    /// <summary>
    /// Puts the dashboard's items in the given order (drag and drop). Ids of other dashboards are ignored; items left
    /// out keep their relative order after the listed ones.
    /// </summary>
    Task ReorderWidgetsAsync(string ownerSid, int dashboardId, IReadOnlyList<int> widgetIds, CancellationToken ct = default);
}

/// <summary>The dashboard limit was reached or the name is invalid – the message is safe to show to the user.</summary>
public sealed class DashboardException(string message) : Exception(message);

/// <summary>A ready-made page of charts to start from.</summary>
public sealed record DashboardTemplate(string Name, string Description, IReadOnlyList<WidgetInput> Widgets);
