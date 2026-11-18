namespace Gaska.Intranet.Core.Analytics;

/// <summary>
/// What the data tabs of a contractor card show: the same metrics as on dashboards, narrowed to the contractor and laid
/// out in a fixed way. The period of every chart follows the tab's period selector; states "as of today" ignore it.
/// </summary>
public static class ContractorLayouts
{
    /// <summary>"Aktywność B2B" – requires <see cref="DataScopes.B2B"/>.</summary>
    public static readonly IReadOnlyList<WidgetInput> B2B =
    [
        Number(MetricCatalog.B2BLogins),
        Number(MetricCatalog.B2BOrders, color: 3),
        Number(MetricCatalog.B2BSearches, color: 7),
        Number(MetricCatalog.B2BPageViews, color: 2),
        Chart(MetricCatalog.B2BLogins, ChartKind.Line, WidgetWidth.Medium),
        Chart(MetricCatalog.B2BOrders, ChartKind.Bars, WidgetWidth.Medium, color: 3),
        Heading("Czego szuka i co ogląda", "Najczęściej szukane frazy i najczęściej klikane towary tego klienta."),
        Chart(MetricCatalog.B2BTopSearches, ChartKind.Bars, WidgetWidth.Medium, color: 7),
        Chart(MetricCatalog.B2BTopProducts, ChartKind.Bars, WidgetWidth.Medium, color: 2),
        Chart(MetricCatalog.B2BTopEmptySearches, ChartKind.Bars, WidgetWidth.Medium, color: 8),
        Chart(MetricCatalog.B2BCartRemovals, ChartKind.Bars, WidgetWidth.Medium, color: 4),
    ];

    /// <summary>"Zwroty i reklamacje" – requires <see cref="DataScopes.Complaints"/>.</summary>
    public static readonly IReadOnlyList<WidgetInput> Complaints =
    [
        Number(MetricCatalog.ComplaintsOpen, color: 8),
        Number(MetricCatalog.ComplaintsOpenReturns, color: 7),
        Number(MetricCatalog.ComplaintsQuality, color: 2),
        Number(MetricCatalog.ComplaintsReturns, color: 7),
        Chart(MetricCatalog.ComplaintsOpenList, ChartKind.Table, WidgetWidth.Full),
        Heading("Zgłoszenia w okresie", "Reklamacje jakościowe, reklamacje dostaw i zwroty tego klienta."),
        Chart(MetricCatalog.ComplaintsQuality, ChartKind.Bars, WidgetWidth.Medium, color: 2),
        Chart(MetricCatalog.ComplaintsReturns, ChartKind.Bars, WidgetWidth.Medium, color: 7),
        Chart(MetricCatalog.ComplaintsDelivery, ChartKind.Bars, WidgetWidth.Medium, color: 4),
        Chart(MetricCatalog.ComplaintsRequests, ChartKind.Donut, WidgetWidth.Medium),
        Heading("Co i dlaczego", "Najczęściej reklamowane i zwracane towary oraz podawane przyczyny."),
        Chart(MetricCatalog.ComplaintsTopProducts, ChartKind.Bars, WidgetWidth.Medium, color: 2),
        Chart(MetricCatalog.ComplaintsTopReturnedProducts, ChartKind.Bars, WidgetWidth.Medium, color: 7),
        Chart(MetricCatalog.ComplaintsTopReasons, ChartKind.Bars, WidgetWidth.Full, color: 8),
    ];

    /// <summary>"Korespondencja" – requires <see cref="DataScopes.Crm"/>.</summary>
    public static readonly IReadOnlyList<WidgetInput> Correspondence =
    [
        Number(MetricCatalog.CrmEmailsIn),
        Number(MetricCatalog.CrmEmailsOut, color: 3),
        Number(MetricCatalog.CrmCalls, color: 7),
        Number(MetricCatalog.CrmMeetings, color: 2),
        Chart(MetricCatalog.CrmCorrespondence, ChartKind.Bars, WidgetWidth.Full, compare: true),
        Heading("Kto i o czym", "Pracownicy, którzy kontaktują się z klientem, i tematy rozmów."),
        Chart(MetricCatalog.CrmByEmployee, ChartKind.Bars, WidgetWidth.Medium, color: 3),
        Chart(MetricCatalog.CrmByTopic, ChartKind.Donut, WidgetWidth.Medium),
        Heading("Spotkania i zadania", "Spotkania z klientem i zadania z nim związane."),
        Chart(MetricCatalog.CrmMeetings, ChartKind.Bars, WidgetWidth.Medium, color: 2),
        Chart(MetricCatalog.CrmTasks, ChartKind.Bars, WidgetWidth.Medium, color: 7),
        Number(MetricCatalog.CrmOverdueTasks, color: 8),
    ];

    private static WidgetInput Heading(string title, string description) =>
        new() { Kind = WidgetKind.Heading, Title = title, Description = description, Width = WidgetWidth.Full };

    private static WidgetInput Number(string metric, int color = 0) =>
        new() { MetricKey = metric, Chart = ChartKind.Number, Width = WidgetWidth.Quarter, Color = color };

    private static WidgetInput Chart(string metric, ChartKind chart, WidgetWidth width, int color = 0, bool compare = false) =>
        new() { MetricKey = metric, Chart = chart, Width = width, Color = color, ComparePrevious = compare, Top = 8 };
}
