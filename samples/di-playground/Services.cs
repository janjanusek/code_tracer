namespace DiPlayground;

// Constructor injection: the service depends on the INTERFACE, never a concrete type.
// `Notify` calls `_notifier.Send(...)` - the call binds to INotifier.Send, not to any impl.
public sealed class NotificationService
{
    private readonly INotifier _notifier;
    public NotificationService(INotifier notifier) => _notifier = notifier;

    public void Notify(string message)
    {
        _notifier.Send(message);               // interface call - DI decides the impl
    }
}

// The top of the chain. PlaceOrder goes through the DI'd service (interface all the way down);
// PlaceOrderDirect uses a direct `new` instead (static analysis already handles this one).
public sealed class OrderController
{
    private readonly NotificationService _service;
    public OrderController(NotificationService service) => _service = service;

    public void PlaceOrder(string customer)
    {
        // OrderController -> NotificationService.Notify -> INotifier.Send -> (impl) -> Audit.Record
        _service.Notify($"order placed for {customer}");
    }

    public void PlaceOrderDirect(string customer)
    {
        // Fully static: new EmailNotifier().Send(...) -> Audit.Record. No interface to bridge.
        var notifier = new EmailNotifier();
        notifier.Send($"order placed for {customer}");
    }

    public void PlaceOrderViaFactory(string customer, string channel)
    {
        // Factory returns an INotifier; the call is again through the interface.
        var notifier = NotifierFactory.Create(channel);
        notifier.Send($"order placed for {customer}");
    }
}
