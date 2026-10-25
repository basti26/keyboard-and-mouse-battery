using System;
internal static class MouseProtocolTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
    private static void Main()
    {
        int value;
        byte[] unavailable = {8,3,0,0,0,1,0,218,151,50,0,0,0,0,0,0,166};
        byte[] available = {8,3,0,0,0,1,1,218,151,50,0,0,0,0,0,0,165};
        byte[] battery = {8,4,0,0,0,2,100,0,16,98,0,0,0,0,0,0,113};
        Check(MouseProtocol.TryValue(unavailable,17,3,1,out value) && value == 0, "Actual unavailable status");
        Check(MouseProtocol.TryValue(available,17,3,1,out value) && value == 1, "Actual recovered status");
        Check(MouseProtocol.TryValue(battery,17,4,100,out value) && value == 100, "Actual recovered battery");
        Check(!MouseProtocol.TryValue(unavailable,17,4,100,out value), "Status must never be mistaken for a battery percentage");
        Check(!MouseProtocol.TryValue(battery,-1,4,100,out value) && !MouseProtocol.TryValue(battery,7,4,100,out value), "Timeout and truncated packets rejected");
        battery[16]++;
        Check(!MouseProtocol.TryValue(battery,17,4,100,out value), "Corrupt checksum rejected");
        Console.WriteLine("Passed 6 mouse protocol checks.");
    }
}
