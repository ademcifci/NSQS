namespace Nsqs.Tests;

public class IndexSchedulerTests
{
    [Fact]
    public void ComputeNextRun_DailyScheduleReturnsFutureTime()
    {
        var schedule = new IndexScheduleSettings
        {
            Kind = IndexScheduleKind.Daily,
            TimeOfDay = "19:00:00"
        };

        var from = new DateTime(2026, 3, 13, 20, 0, 0);
        var next = IndexScheduler.ComputeNextRun(schedule, from);

        Assert.Equal(new DateTime(2026, 3, 14, 19, 0, 0), next);
    }

    [Fact]
    public void ComputeLastDueRun_WeeklyScheduleReturnsPreviousOccurrence()
    {
        var schedule = new IndexScheduleSettings
        {
            Enabled = true,
            Kind = IndexScheduleKind.Weekly,
            DayOfWeek = DayOfWeek.Tuesday,
            TimeOfDay = "19:00:00"
        };

        var from = new DateTime(2026, 3, 13, 20, 0, 0); // Friday
        var lastDue = IndexScheduler.ComputeLastDueRun(schedule, from);

        Assert.Equal(new DateTime(2026, 3, 10, 19, 0, 0), lastDue);
    }
}
