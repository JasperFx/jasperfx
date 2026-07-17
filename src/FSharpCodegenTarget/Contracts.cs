namespace FSharpCodegenTarget;

/// <summary>
///     The interface the generated F# type implements — stands in for a Wolverine
///     handler/endpoint contract.
/// </summary>
public interface IGreeter
{
    string Greet(string name);
}

/// <summary>
///     The async counterpart of <see cref="IGreeter" />, used to exercise the F# <c>task { }</c>
///     emit path.
/// </summary>
public interface IAsyncGreeter
{
    Task<string> GreetAsync(string name);
}

/// <summary>
///     Async interface whose generated implementation just returns the trailing Task expression
///     directly — used to exercise the idiomatic <c>ReturnFromLastNode</c> path (no <c>task { }</c>).
/// </summary>
public interface IDirectAsyncGreeter
{
    Task<string> GreetDirectAsync(string name);
}

/// <summary>
///     A mutable value object, reassigned in the generated method to exercise the F#
///     <c>let mutable</c> / <c>&lt;-</c> path.
/// </summary>
public interface IAccumulator
{
    MutableBox Accumulate();
}

public class MutableBox
{
    public int Value { get; set; }
}

public class AccumulatorService
{
    public MutableBox Advance(MutableBox box)
    {
        box.Value++;
        return box;
    }
}

// Control-flow contracts: exercise IfElseNullGuardFrame, IfBlock, and TryFinallyWrapperFrame.

public interface IConditionalGreeter
{
    string Describe(string input);
}

/// <summary>
///     The same null-guard, but over an F# class type that is not <c>[&lt;AllowNullLiteral&gt;]</c> —
///     the shape of an F# Wolverine saga. F#'s <c>isNull</c> carries a <c>'T : null</c> constraint that
///     such a type does not satisfy, so the guard must box its subject to compile (jasperfx#513).
///     <see cref="IConditionalGreeter" /> cannot cover this: its subject is a <c>string</c>, which is
///     null-permitting in F# and so compiles either way.
/// </summary>
public interface IFSharpSagaGuard
{
    string Describe(FSharpTypes.FSharpSaga saga);
}

public class SagaService
{
    public string Fallback()
    {
        return "no saga";
    }

    public string Echo(FSharpTypes.FSharpSaga saga)
    {
        return saga.Name;
    }
}

public interface IToggle
{
    void Toggle(bool flag);
}

public interface IResource
{
    void Run();
}

/// <summary>
///     A handler whose signature returns a (non-generic) Task but whose body is synchronous —
///     exercises the AsyncMode.None + Task return path that must emit <c>Task.CompletedTask</c>.
/// </summary>
public interface ISyncTaskHandler
{
    Task HandleAsync(string name);
}

/// <summary>
///     A scoped dependency + its consumer. Registering <see cref="IScopedThing" /> as an opaque
///     <c>AddScoped&lt;T&gt;(_ =&gt; …)</c> lambda factory forces JasperFx to resolve it via service
///     location, so the generated <see cref="IScopedConsumerHandler" /> implementation emits the
///     scoped-DI frames (ScopedContainerCreation + GetServiceFromScopedContainerFrame). The async
///     <c>DoAsync</c> makes the handler a <c>task { }</c> body, exercising the
///     <c>use … CreateAsyncScope()</c> path. See jasperfx#397.
/// </summary>
public interface IScopedThing
{
    Task DoAsync();
}

public class ScopedThing : IScopedThing
{
    public Task DoAsync()
    {
        return Task.CompletedTask;
    }
}

public interface IScopedConsumerHandler
{
    Task Handle();
}

// --- Contracts for the remaining JasperFx F# frame surface (jasperfx#399) ---

/// <summary>
///     A sync void handler used to exercise <c>AppendActivityEventFrame</c>: the generated body emits a
///     guarded <c>Activity.Current.AddEvent(...)</c> (F# has no <c>?.</c>, so an explicit isNull guard).
/// </summary>
public interface IActivityEmitter
{
    void Emit();
}

/// <summary>
///     A handler that stamps the current time. The injected <see cref="DateTime" /> resolves through
///     <c>NowTimeVariableSource</c> → <c>NowFetchFrame</c> (<c>let now = System.DateTime.UtcNow</c>).
/// </summary>
public interface INowHandler
{
    string Stamp();
}

public class ClockService
{
    public string Stamp(DateTime now)
    {
        return now.ToString("O");
    }
}

/// <summary>
///     A handler whose signature returns <see cref="ValueTask{T}" /> with a synchronous body — exercises
///     <c>ReturnValueTask</c>: the trailing F# expression constructs a <c>ValueTask&lt;string&gt;(result)</c>.
/// </summary>
public interface IValueTaskHandler
{
    ValueTask<string> HandleAsync();
}

/// <summary>
///     A handler that reads a member off a constructed object — exercises <c>MemberAccessFrame</c>
///     (<c>let value = mutableBox.Value</c>).
/// </summary>
public interface IMemberAccessHandler
{
    int Read();
}

/// <summary>
///     A handler that builds an array of injected elements — exercises <c>CreateArrayFrame</c>'s F# array
///     literal (<c>let things = [| thingA; thingB |]</c>).
/// </summary>
public interface IArrayHandler
{
    Thing[] Build();
}

/// <summary>
///     A handler that resolves a service flagged <c>AlwaysUseServiceLocationFor</c> off an injected
///     <see cref="IServiceProvider" /> — exercises <c>LazyServiceLocationFrame</c>
///     (<c>let controlFlowService = ServiceProviderServiceExtensions.GetRequiredService&lt;…&gt;(provider)</c>).
/// </summary>
public interface ILazyHandler
{
    string Handle();
}

/// <summary>
///     A base class with a public instance method (<see cref="Bump" />) and an abstract method to
///     override (<see cref="Compute" />). The generated subclass overrides <c>Compute</c> and calls the
///     inherited instance <c>Bump</c> via a local (this-qualified) MethodCall — exercises the named-self
///     F# emit so inherited instance members resolve (jasperfx#393).
/// </summary>
public abstract class CalculatorBase
{
    public int Bump(int value)
    {
        return value + 1;
    }

    public abstract int Compute(int seed);
}

/// <summary>
///     A concrete type, its interface, and a service taking the interface. The generated handler
///     constructs the concrete type and passes it to the service through a <c>CastVariable</c> (upcast
///     to the interface) — exercises the F# cast operator (<c>:&gt;</c>) for CastVariable (jasperfx#395).
/// </summary>
public interface IThing;

public class Thing : IThing;

public class ThingDescriber
{
    public string Describe(IThing thing)
    {
        return thing.GetType().Name;
    }
}

public interface IThingHandler
{
    string Handle();
}

public class ControlFlowService
{
    public string Fallback()
    {
        return "fallback";
    }

    public string Echo(string input)
    {
        return input;
    }

    public void Record()
    {
    }

    public void Begin()
    {
    }

    public void End()
    {
    }
}

// A Wolverine-shaped message handler: construct a domain object, await a repository
// save, build a confirmation, return it. Exercises a realistic multi-frame async body.

public class PlaceOrder
{
    public PlaceOrder(string productId, int quantity)
    {
        ProductId = productId;
        Quantity = quantity;
    }

    public string ProductId { get; }
    public int Quantity { get; }
}

public class Order
{
    public Order(PlaceOrder command)
    {
        ProductId = command.ProductId;
    }

    public string ProductId { get; }
}

public class OrderConfirmation
{
    public OrderConfirmation(Order order)
    {
        ProductId = order.ProductId;
    }

    public string ProductId { get; }
}

public interface IOrderRepository
{
    Task SaveAsync(Order order);
}

public class ConfirmationFactory
{
    public OrderConfirmation Create(Order order)
    {
        return new OrderConfirmation(order);
    }
}

public interface IOrderHandler
{
    Task<OrderConfirmation> Handle(PlaceOrder command);
}

/// <summary>
///     A simple value object the generated method constructs (exercises ConstructorFrame).
/// </summary>
public class Salutation
{
    public Salutation(string name)
    {
        Text = "Hello " + name;
    }

    public string Text { get; }
}

/// <summary>
///     A dependency the generated type takes through its constructor and calls
///     (exercises constructor injection + MethodCall).
/// </summary>
public class GreetingService
{
    public string CreateGreeting(Salutation salutation)
    {
        return salutation.Text + "!";
    }

    public Task<string> CreateGreetingAsync(Salutation salutation)
    {
        return Task.FromResult(salutation.Text + "!");
    }
}
