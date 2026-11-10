namespace Gaska.Intranet.Core.Rcp;

/// <summary>
/// Kind of a time-clock record – values match <c>idx_wartosci</c> of the Punktualnik function keys
/// (<c>dev.klawisze_funkc</c>): 0 entry, 1 exit, 2 back from break, 3 break.
/// </summary>
public enum RcpEventType
{
    WorkStart = 0,
    WorkEnd = 1,
    BreakEnd = 2,
    BreakStart = 3,
}

/// <summary>Employee's state derived from today's records.</summary>
public enum RcpState
{
    NotStarted,
    Working,
    OnBreak,
    Finished,
}

/// <summary>Single record from the time clock (terminal scan or a remote punch).</summary>
public sealed record RcpEvent(DateTime Time, RcpEventType Type);

public sealed record RcpBreak(DateTime Start, DateTime? End)
{
    public TimeSpan Duration(DateTime now) => (End ?? (now > Start ? now : Start)) - Start;
}

/// <summary>Person in the Punktualnik system linked to the signed-in user.</summary>
public sealed record RcpEmployee(int Id, string FullName);

/// <summary>
/// One day of time-clock records with derived values. Records are replayed in order, so repeated
/// entries/exits (two shifts, a forgotten exit) and unmatched break records are handled gracefully.
/// </summary>
public sealed class RcpDay
{
    private readonly List<(DateTime Start, DateTime? End)> _work = [];
    private readonly List<RcpBreak> _breaks = [];

    public RcpDay(DateOnly date, IEnumerable<RcpEvent> events)
    {
        Date = date;
        Events = events.OrderBy(e => e.Time).ToList();

        DateTime? workStart = null;
        DateTime? breakStart = null;
        foreach (var e in Events)
        {
            switch (e.Type)
            {
                case RcpEventType.WorkStart when workStart is null:
                    workStart = e.Time;
                    break;
                case RcpEventType.WorkEnd when workStart is not null:
                    CloseBreak(ref breakStart, e.Time);
                    _work.Add((workStart.Value, e.Time));
                    workStart = null;
                    break;
                case RcpEventType.BreakStart when breakStart is null:
                    breakStart = e.Time;
                    break;
                case RcpEventType.BreakEnd when breakStart is not null:
                    CloseBreak(ref breakStart, e.Time);
                    break;
            }
        }

        if (workStart is not null)
        {
            _work.Add((workStart.Value, null));
        }

        if (breakStart is not null)
        {
            _breaks.Add(new RcpBreak(breakStart.Value, null));
        }
    }

    public DateOnly Date { get; }

    public IReadOnlyList<RcpEvent> Events { get; }

    public IReadOnlyList<RcpBreak> Breaks => _breaks;

    /// <summary>First entry of the day.</summary>
    public DateTime? FirstStart => _work.Count == 0 ? null : _work[0].Start;

    /// <summary>Last exit – only when the employee is not working any more.</summary>
    public DateTime? LastEnd => _work.Count == 0 || _work[^1].End is null ? null : _work[^1].End;

    public RcpState State =>
        _work.Count == 0 ? RcpState.NotStarted
        : _work[^1].End is not null ? RcpState.Finished
        : _breaks.Count > 0 && _breaks[^1].End is null ? RcpState.OnBreak
        : RcpState.Working;

    public RcpBreak? CurrentBreak => State == RcpState.OnBreak ? _breaks[^1] : null;

    /// <summary>Time between entries and exits (open shift counted until <paramref name="now"/>).</summary>
    public TimeSpan PresenceTime(DateTime now) =>
        _work.Aggregate(TimeSpan.Zero, (sum, w) => sum + ((w.End ?? Max(now, w.Start)) - w.Start));

    public TimeSpan BreakTime(DateTime now) =>
        _breaks.Aggregate(TimeSpan.Zero, (sum, b) => sum + b.Duration(now));

    /// <summary>Presence minus breaks.</summary>
    public TimeSpan WorkTime(DateTime now)
    {
        var work = PresenceTime(now) - BreakTime(now);
        return work < TimeSpan.Zero ? TimeSpan.Zero : work;
    }

    /// <summary>Whether a punch of the given kind makes sense now (blocks double clicks and impossible sequences).</summary>
    public bool CanPunch(RcpEventType type) => (State, type) switch
    {
        (RcpState.NotStarted or RcpState.Finished, RcpEventType.WorkStart) => true,
        (RcpState.Working, RcpEventType.WorkEnd or RcpEventType.BreakStart) => true,
        (RcpState.OnBreak, RcpEventType.BreakEnd) => true,
        _ => false,
    };

    private void CloseBreak(ref DateTime? breakStart, DateTime end)
    {
        if (breakStart is not null)
        {
            _breaks.Add(new RcpBreak(breakStart.Value, end));
            breakStart = null;
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>Time clock (Punktualnik) – reading records and remote punches.</summary>
public interface IRcpService
{
    /// <summary>Finds the time-clock person of a domain user (configured override or first + last name).</summary>
    Task<RcpEmployee?> FindEmployeeAsync(string accountName, string displayName, CancellationToken ct = default);

    /// <summary>Days from <paramref name="from"/> to <paramref name="to"/> (inclusive), newest first; days without records are skipped.</summary>
    Task<IReadOnlyList<RcpDay>> GetDaysAsync(int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>Registers a punch with the current server time; refuses sequences that make no sense (e.g. a second entry).</summary>
    Task<RcpDay> PunchAsync(int employeeId, RcpEventType type, CancellationToken ct = default);
}

/// <summary>Time clock error with a message for the user (Polish).</summary>
public sealed class RcpException(string message, Exception? inner = null) : Exception(message, inner);
