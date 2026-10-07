namespace TokenChecker.Core;

public static class BackoffSchedule
{
    public static TimeSpan GetDelay(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(5),
        4 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(15)
    };
}
