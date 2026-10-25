using System;
internal static class BatteryHistoryTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
    private static void Main()
    {
        var history = new BatteryHistory();
        var start = new DateTimeOffset(2026,10,26,1,0,0,TimeSpan.FromHours(8));
        history.Observe(null,start);
        Check(!history.Value.HasValue && !history.UpdatedAt.HasValue && !history.IsStale, "No invented value on initial failure");
        history.Observe(65,start);
        Check(history.Value==65 && history.UpdatedAt==start && !history.IsStale, "Fresh success");
        history.Observe(null,start.AddMinutes(1));
        history.Observe(null,start.AddMinutes(2));
        Check(history.Value==65 && history.UpdatedAt==start && history.IsStale, "Failures preserve percentage and actual sample time");
        history.Observe(42,start.AddMinutes(3));
        Check(history.Value==42 && history.UpdatedAt==start.AddMinutes(3) && !history.IsStale, "Recovery replaces cached value and clears stale flag");
        history.Observe(0,start.AddMinutes(4));
        history.Observe(null,start.AddMinutes(5));
        Check(history.Value==0 && history.UpdatedAt==start.AddMinutes(4) && history.IsStale, "Zero percent is preserved correctly");
        Console.WriteLine("Passed 5 battery history checks.");
    }
}
