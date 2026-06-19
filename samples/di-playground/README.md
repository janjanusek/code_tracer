# DI playground — a test fixture for tracing through dependency injection

A tiny, self-contained C# project full of the patterns that "should" defeat a static call-graph,
used to prove that CodeTracer traces through them. It is **not** part of CodeTracer (it has its
own `.csproj`/`.sln`; CodeTracer excludes `samples/**` from its own build).

The chain everything funnels into is `Audit.Record`. The patterns:

| pattern | where | does CodeTracer trace it? |
|---|---|---|
| **Interface + constructor injection** | `NotificationService(INotifier)` → `_notifier.Send()` | ✅ yes — bridges `INotifier.Send` to its implementations |
| **Multiple implementations** | `EmailNotifier`, `SmsNotifier`, `LoggingNotifier` | ✅ yes — `--all-paths` lists each |
| **Decorator** (impl wraps another impl) | `LoggingNotifier` holds an `INotifier` | ✅ yes — nested paths |
| **Factory** returning the interface | `NotifierFactory.Create()` | ✅ yes |
| **Direct `new`** | `OrderController.PlaceOrderDirect` | ✅ yes (plain static call) |

Try it:

```bash
# all implementation paths through the interface:
dotnet run -- trace -s samples/di-playground/DiPlayground.sln \
  --from "NotificationService.Notify" --to "Audit.Record" --all-paths --no-llm

# the direct (non-interface) chain:
dotnet run -- trace -s samples/di-playground/DiPlayground.sln \
  --from "OrderController.PlaceOrderDirect" --to "Audit.Record" --no-llm
```

See [`../../examples/trace-di-multiple-impls.md`](../../examples/trace-di-multiple-impls.md) for the output.
