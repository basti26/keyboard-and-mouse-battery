using System;
using System.IO;
using System.Linq;
using System.Threading;

internal static class LogTests
{
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    private static void Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log-tests-" + Guid.NewGuid().ToString("N"));
        string rotation = Path.Combine(root, "rotation");
        for (int i = 0; i < 30; i++) AppLog.WriteTo(rotation, "entry=" + i + " " + new string('x', 80), 512);
        string[] files = Directory.GetFiles(rotation);
        Check(files.Length == 3 && files.All(delegate(string p) { return new FileInfo(p).Length <= 512; }), "Three bounded log files");
        Check(File.ReadAllText(Path.Combine(rotation, "battery.log")).Contains("entry=29"), "Newest entry survives rotation");

        string single = Path.Combine(root, "single-line");
        AppLog.WriteTo(single, "鼠标超时\r\nretry", 4096);
        string[] lines = File.ReadAllLines(Path.Combine(single, "battery.log"));
        Check(lines.Length == 1 && lines[0].Contains("鼠标超时\\r\\nretry") && lines[0].Contains("pid="), "UTF-8 timestamps and single-line records");

        string concurrent = Path.Combine(root, "concurrent");
        Thread[] writers = new Thread[4];
        for (int i = 0; i < writers.Length; i++) {
            writers[i] = new Thread(delegate() { for (int n = 0; n < 10; n++) AppLog.WriteTo(concurrent, "parallel entry", 65536); });
            writers[i].Start();
        }
        foreach (Thread writer in writers) writer.Join();
        Check(File.ReadAllLines(Path.Combine(concurrent, "battery.log")).Length == 40, "Concurrent entries are complete");

        string blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "This is a file, not a directory.");
        AppLog.WriteTo(blocked, "Must not throw on IO failure", 512);
        Console.WriteLine("Passed 5 logging checks.");
    }
}
