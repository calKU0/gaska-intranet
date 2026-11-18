using Gaska.Intranet.Core.Analytics;
using Gaska.Intranet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Gaska.Intranet.Infrastructure.Analytics;

/// <summary>Dashboards in the application database. Every query filters by the owner's SID.</summary>
internal sealed class DashboardService(IDbContextFactory<IntranetDbContext> dbFactory, TimeProvider clock) : IDashboardService
{
    private const int MaxNameLength = 60;

    public async Task<IReadOnlyList<Dashboard>> GetAsync(string ownerSid, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var dashboards = await db.Dashboards.AsNoTracking()
            .Include(d => d.Widgets)
            .Where(d => d.OwnerSid == ownerSid)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .ToListAsync(ct);

        foreach (var dashboard in dashboards)
        {
            dashboard.Widgets = [.. dashboard.Widgets.OrderBy(w => w.SortOrder).ThenBy(w => w.Id)];
        }

        return dashboards;
    }

    public async Task<Dashboard> CreateAsync(
        string ownerSid, string ownerAccount, string name, IReadOnlyList<WidgetInput>? widgets = null, CancellationToken ct = default)
    {
        RequireOwner(ownerSid);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Dashboards.Where(d => d.OwnerSid == ownerSid).Select(d => d.SortOrder).ToListAsync(ct);
        if (existing.Count >= IDashboardService.MaxDashboards)
        {
            throw new DashboardException($"Możesz mieć najwyżej {IDashboardService.MaxDashboards} stron. Usuń którąś, aby dodać nową.");
        }

        var dashboard = new Dashboard
        {
            OwnerSid = ownerSid,
            OwnerAccount = ownerAccount,
            Name = CleanName(name),
            SortOrder = existing.Count == 0 ? 0 : existing.Max() + 1,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };

        var order = 0;
        foreach (var input in (widgets ?? []).Take(IDashboardService.MaxWidgets))
        {
            var widget = new DashboardWidget { SortOrder = order++ };
            Apply(widget, input);
            dashboard.Widgets.Add(widget);
        }

        db.Dashboards.Add(dashboard);
        await db.SaveChangesAsync(ct);
        return dashboard;
    }

    public async Task RenameAsync(string ownerSid, int dashboardId, string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var dashboard = await db.Dashboards.FirstOrDefaultAsync(d => d.Id == dashboardId && d.OwnerSid == ownerSid, ct);
        if (dashboard is null)
        {
            return;
        }

        dashboard.Name = CleanName(name);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string ownerSid, int dashboardId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var dashboard = await db.Dashboards.Include(d => d.Widgets)
            .FirstOrDefaultAsync(d => d.Id == dashboardId && d.OwnerSid == ownerSid, ct);
        if (dashboard is null)
        {
            return;
        }

        db.Dashboards.Remove(dashboard);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DashboardWidget> AddWidgetAsync(string ownerSid, int dashboardId, WidgetInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var dashboard = await db.Dashboards.Include(d => d.Widgets)
            .FirstOrDefaultAsync(d => d.Id == dashboardId && d.OwnerSid == ownerSid, ct)
            ?? throw new DashboardException("Ta strona już nie istnieje. Odśwież widok.");

        if (dashboard.Widgets.Count >= IDashboardService.MaxWidgets)
        {
            throw new DashboardException($"Na jednej stronie mieści się najwyżej {IDashboardService.MaxWidgets} wykresów. Dodaj kolejną stronę.");
        }

        var widget = new DashboardWidget
        {
            SortOrder = dashboard.Widgets.Count == 0 ? 0 : dashboard.Widgets.Max(w => w.SortOrder) + 1,
        };
        Apply(widget, input);
        dashboard.Widgets.Add(widget);
        await db.SaveChangesAsync(ct);
        return widget;
    }

    public async Task UpdateWidgetAsync(string ownerSid, int widgetId, WidgetInput input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await FindWidgetAsync(db, ownerSid, widgetId, ct) is not { } widget)
        {
            return;
        }

        Apply(widget, input);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveWidgetAsync(string ownerSid, int widgetId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await FindWidgetAsync(db, ownerSid, widgetId, ct) is not { } widget)
        {
            return;
        }

        db.DashboardWidgets.Remove(widget);
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveWidgetAsync(string ownerSid, int widgetId, int direction, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await FindWidgetAsync(db, ownerSid, widgetId, ct) is not { } widget)
        {
            return;
        }

        var widgets = await db.DashboardWidgets
            .Where(w => w.DashboardId == widget.DashboardId)
            .OrderBy(w => w.SortOrder).ThenBy(w => w.Id)
            .ToListAsync(ct);

        var index = widgets.FindIndex(w => w.Id == widgetId);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= widgets.Count)
        {
            return;
        }

        (widgets[index], widgets[target]) = (widgets[target], widgets[index]);
        for (var i = 0; i < widgets.Count; i++)
        {
            widgets[i].SortOrder = i;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ReorderWidgetsAsync(string ownerSid, int dashboardId, IReadOnlyList<int> widgetIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var widgets = await db.DashboardWidgets
            .Where(w => w.DashboardId == dashboardId && db.Dashboards.Any(d => d.Id == dashboardId && d.OwnerSid == ownerSid))
            .OrderBy(w => w.SortOrder).ThenBy(w => w.Id)
            .ToListAsync(ct);

        // Listed items first, in the given order; anything not listed (added in another tab meanwhile) keeps its place after them.
        var position = widgetIds.Distinct().Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var ordered = widgets.OrderBy(w => position.GetValueOrDefault(w.Id, int.MaxValue)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The widget, only when its dashboard belongs to the owner.</summary>
    private static Task<DashboardWidget?> FindWidgetAsync(IntranetDbContext db, string ownerSid, int widgetId, CancellationToken ct) =>
        db.DashboardWidgets.FirstOrDefaultAsync(
            w => w.Id == widgetId && db.Dashboards.Any(d => d.Id == w.DashboardId && d.OwnerSid == ownerSid), ct);

    private static void Apply(DashboardWidget widget, WidgetInput input)
    {
        widget.Kind = input.Kind;
        widget.MetricKey = input.Kind == WidgetKind.Heading ? string.Empty : input.MetricKey;
        widget.Title = Clean(input.Title, 80);
        widget.Description = input.Kind == WidgetKind.Heading ? Clean(input.Description, 200) : null;
        widget.Color = Math.Clamp(input.Color, 0, 8);
        widget.Chart = input.Chart;
        widget.Period = input.Period;
        widget.CustomFrom = input.Period == PeriodPreset.Custom ? input.CustomFrom : null;
        widget.CustomTo = input.Period == PeriodPreset.Custom ? input.CustomTo : null;
        widget.Granularity = input.Granularity;
        widget.Width = input.Kind == WidgetKind.Heading ? WidgetWidth.Full : input.Width;
        widget.ComparePrevious = input.ComparePrevious;
        widget.Top = Math.Clamp(input.Top, 3, 30);
    }

    private static string? Clean(string? text, int maxLength)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string CleanName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new DashboardException("Podaj nazwę strony.");
        }

        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    private static void RequireOwner(string ownerSid)
    {
        if (string.IsNullOrWhiteSpace(ownerSid))
        {
            throw new DashboardException("Nie udało się ustalić użytkownika. Zaloguj się ponownie.");
        }
    }
}
