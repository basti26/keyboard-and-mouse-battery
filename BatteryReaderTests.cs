using System;

internal static class BatteryReaderTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
    }
    private static void Main()
    {
        int m = 0, k = 0, waits = 0;
        BatterySnapshot s = BatteryReader.Read(delegate { m++; return 0; }, delegate { k++; return 100; },
            delegate { waits++; }, delegate { return false; });
        Check(s.Mouse == 0 && s.Keyboard == 100 && m == 1 && k == 1 && waits == 0, "Zero is valid; no redundant reads");

        m = k = waits = 0;
        s = BatteryReader.Read(delegate { return ++m == 1 ? (int?)null : 65; }, delegate { k++; return 90; },
            delegate { waits++; }, delegate { return false; });
        Check(s.Mouse == 65 && s.Keyboard == 90 && m == 2 && k == 1 && waits == 1, "Recover only missing mouse");

        m = k = waits = 0;
        s = BatteryReader.Read(delegate { m++; return 65; }, delegate { if (++k == 1) throw new Exception("Disconnected"); return 85; },
            delegate { waits++; }, delegate { return false; });
        Check(s.Mouse == 65 && s.Keyboard == 85 && m == 1 && k == 2 && waits == 1, "Recover keyboard exception independently");

        m = k = waits = 0;
        s = BatteryReader.Read(delegate { m++; throw new Exception("Absent"); }, delegate { k++; return null; },
            delegate { waits++; }, delegate { return false; });
        Check(!s.Mouse.HasValue && !s.Keyboard.HasValue && m == 2 && k == 2 && waits == 1, "Permanent failure is bounded");

        bool stopped = false;
        m = k = 0;
        s = BatteryReader.Read(delegate { m++; return null; }, delegate { k++; return null; },
            delegate { stopped = true; }, delegate { return stopped; });
        Check(m == 1 && k == 1, "Do not retry after shutdown");
        Console.WriteLine("Passed 5 battery recovery checks.");
    }
}
