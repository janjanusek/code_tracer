namespace DiPlayground;

// The interface that DI resolves at runtime. Static analysis can't know which
// implementation a container will inject - this is the case CodeTracer must bridge.
public interface INotifier
{
    void Send(string message);
}

// Implementation #1 - its Send() reaches the deep target (Audit.Record).
public sealed class EmailNotifier : INotifier
{
    public void Send(string message)
    {
        var clean = Formatter.Clean(message);
        Audit.Record("email", clean);          // <-- the target of the trace
    }
}

// Implementation #2 - a second impl, so resolving the interface is genuinely ambiguous.
public sealed class SmsNotifier : INotifier
{
    public void Send(string message)
    {
        Audit.Record("sms", message.Substring(0, System.Math.Min(message.Length, 160)));
    }
}

// A decorator: it holds ANOTHER INotifier and calls it through the interface (interface->impl
// again, one level deeper) before doing its own work.
public sealed class LoggingNotifier : INotifier
{
    private readonly INotifier _inner;
    public LoggingNotifier(INotifier inner) => _inner = inner;

    public void Send(string message)
    {
        Audit.Record("log", $"about to send: {message}");
        _inner.Send(message);                  // interface call to the wrapped notifier
    }
}

// A factory that returns an INotifier - the concrete type is chosen at runtime by a string.
public static class NotifierFactory
{
    public static INotifier Create(string kind) => kind switch
    {
        "email" => new EmailNotifier(),
        "sms"   => new SmsNotifier(),
        _       => new LoggingNotifier(new EmailNotifier()),
    };
}
