namespace DiPlayground;

// The deep target of the traces. Everything ultimately funnels here.
public static class Audit
{
    private static int _count;

    public static void Record(string channel, string payload)
    {
        _count++;
        Sink.Write($"[{_count}] {channel}: {payload}");
    }
}

public static class Formatter
{
    public static string Clean(string s) => s.Trim().Replace("\r", "").Replace("\n", " ");
}

// A tiny leaf so the chain has one more concrete hop below Audit.Record.
public static class Sink
{
    public static void Write(string line) => System.Console.WriteLine(line);
}
