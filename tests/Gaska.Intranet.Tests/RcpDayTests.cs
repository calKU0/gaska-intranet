using Gaska.Intranet.Core.Rcp;

namespace Gaska.Intranet.Tests;

public sealed class RcpDayTests
{
    private static readonly DateOnly Day = new(2026, 11, 5);

    private static DateTime At(int hour, int minute) => Day.ToDateTime(new TimeOnly(hour, minute));

    private static RcpDay Build(params (int H, int M, RcpEventType Type)[] events) =>
        new(Day, events.Select(e => new RcpEvent(At(e.H, e.M), e.Type)));

    [Fact]
    public void Empty_day_allows_only_work_start()
    {
        var day = Build();

        Assert.Equal(RcpState.NotStarted, day.State);
        Assert.True(day.CanPunch(RcpEventType.WorkStart));
        Assert.False(day.CanPunch(RcpEventType.WorkEnd));
        Assert.False(day.CanPunch(RcpEventType.BreakStart));
        Assert.False(day.CanPunch(RcpEventType.BreakEnd));
    }

    [Fact]
    public void Open_break_is_counted_until_now()
    {
        var day = Build((7, 0, RcpEventType.WorkStart), (10, 0, RcpEventType.BreakStart));
        var now = At(10, 20);

        Assert.Equal(RcpState.OnBreak, day.State);
        Assert.Equal(TimeSpan.FromMinutes(20), day.CurrentBreak!.Duration(now));
        Assert.Equal(TimeSpan.FromMinutes(180), day.WorkTime(now)); // 3 h 20 min presence − 20 min break
        Assert.True(day.CanPunch(RcpEventType.BreakEnd));
        Assert.False(day.CanPunch(RcpEventType.WorkEnd));
    }

    [Fact]
    public void Finished_day_sums_breaks_and_work()
    {
        var day = Build(
            (15, 0, RcpEventType.WorkEnd),
            (7, 0, RcpEventType.WorkStart), // records arrive unordered
            (9, 0, RcpEventType.BreakStart),
            (9, 15, RcpEventType.BreakEnd),
            (12, 0, RcpEventType.BreakStart),
            (12, 30, RcpEventType.BreakEnd));
        var now = At(18, 0);

        Assert.Equal(RcpState.Finished, day.State);
        Assert.Equal(At(7, 0), day.FirstStart);
        Assert.Equal(At(15, 0), day.LastEnd);
        Assert.Equal(2, day.Breaks.Count);
        Assert.Equal(TimeSpan.FromMinutes(45), day.BreakTime(now));
        Assert.Equal(TimeSpan.FromHours(8), day.PresenceTime(now));
        Assert.Equal(TimeSpan.FromMinutes(435), day.WorkTime(now));
    }

    [Fact]
    public void Duplicate_and_orphan_records_are_ignored()
    {
        var day = Build(
            (6, 50, RcpEventType.BreakEnd),   // back from break without a break
            (7, 0, RcpEventType.WorkStart),
            (7, 1, RcpEventType.WorkStart),   // double scan
            (11, 0, RcpEventType.BreakStart),
            (11, 1, RcpEventType.BreakStart));

        Assert.Equal(RcpState.OnBreak, day.State);
        Assert.Equal(At(7, 0), day.FirstStart);
        Assert.Single(day.Breaks);
        Assert.Equal(At(11, 0), day.Breaks[0].Start);
    }

    [Fact]
    public void Exit_during_break_closes_the_break()
    {
        var day = Build((7, 0, RcpEventType.WorkStart), (13, 0, RcpEventType.BreakStart), (13, 10, RcpEventType.WorkEnd));

        Assert.Equal(RcpState.Finished, day.State);
        Assert.Equal(TimeSpan.FromMinutes(10), day.BreakTime(At(20, 0)));
    }

    [Fact]
    public void Second_shift_after_exit_is_counted()
    {
        var day = Build(
            (7, 0, RcpEventType.WorkStart), (11, 0, RcpEventType.WorkEnd),
            (13, 0, RcpEventType.WorkStart));
        var now = At(14, 0);

        Assert.Equal(RcpState.Working, day.State);
        Assert.Null(day.LastEnd);
        Assert.Equal(TimeSpan.FromHours(5), day.WorkTime(now));
    }
}
