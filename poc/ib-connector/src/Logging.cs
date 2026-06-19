namespace IbPoc;

internal static class Log
{
    public static void Line(string msg) =>
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {msg}");
}
