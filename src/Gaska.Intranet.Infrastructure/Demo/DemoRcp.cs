using Gaska.Intranet.Core.Rcp;

namespace Gaska.Intranet.Infrastructure.Demo;

/// <summary>
/// Demo time clock (Development + Demo:Enabled): a normal working day so far and a short history, in memory.
/// Used for the user manual's screenshots and for UI work without Punktualnik.
/// </summary>
internal sealed class DemoRcp(TimeProvider clock) : IRcpService
{
    private readonly object _gate = new();
    private readonly Dictionary<DateOnly, List<RcpEvent>> _events = Seed(clock.GetLocalNow().DateTime);

    public Task<RcpEmployee?> FindEmployeeAsync(string accountName, string displayName, CancellationToken ct = default) =>
        Task.FromResult<RcpEmployee?>(new RcpEmployee(1, string.IsNullOrWhiteSpace(displayName) ? accountName : displayName));

    public Task<IReadOnlyList<RcpDay>> GetDaysAsync(int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // Same contract as Punktualnik: only days with records, newest first.
            var days = _events
                .Where(e => e.Key >= from && e.Key <= to && e.Value.Count > 0)
                .OrderByDescending(e => e.Key)
                .Select(e => new RcpDay(e.Key, e.Value.ToList()))
                .ToList();

            return Task.FromResult<IReadOnlyList<RcpDay>>(days);
        }
    }

    public Task<RcpDay> PunchAsync(int employeeId, RcpEventType type, CancellationToken ct = default)
    {
        var now = clock.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);
        lock (_gate)
        {
            if (!_events.TryGetValue(today, out var list))
            {
                _events[today] = list = [];
            }

            if (!new RcpDay(today, list).CanPunch(type))
            {
                throw new RcpException("Tego odbicia nie można teraz wykonać.");
            }

            list.Add(new RcpEvent(now, type));
            return Task.FromResult(new RcpDay(today, list.ToList()));
        }
    }

    private static Dictionary<DateOnly, List<RcpEvent>> Seed(DateTime now)
    {
        var result = new Dictionary<DateOnly, List<RcpEvent>>();
        var today = now.Date;

        // Today: started at 7:52, a 20-minute break done, working now.
        var start = today.AddHours(7).AddMinutes(52);
        var todayEvents = new List<RcpEvent> { new(start, RcpEventType.WorkStart) };
        if (now > today.AddHours(10).AddMinutes(25))
        {
            todayEvents.Add(new RcpEvent(today.AddHours(10).AddMinutes(5), RcpEventType.BreakStart));
            todayEvents.Add(new RcpEvent(today.AddHours(10).AddMinutes(25), RcpEventType.BreakEnd));
        }

        if (now > start)
        {
            result[DateOnly.FromDateTime(today)] = todayEvents;
        }

        // The previous working days: full days with a break.
        var day = today.AddDays(-1);
        for (var added = 0; added < 6; day = day.AddDays(-1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var minute = added * 7 % 15;
            result[DateOnly.FromDateTime(day)] =
            [
                new(day.AddHours(7).AddMinutes(45 + minute), RcpEventType.WorkStart),
                new(day.AddHours(10).AddMinutes(minute), RcpEventType.BreakStart),
                new(day.AddHours(10).AddMinutes(18 + minute), RcpEventType.BreakEnd),
                new(day.AddHours(15).AddMinutes(50 + minute), RcpEventType.WorkEnd),
            ];
            added++;
        }

        return result;
    }
}
