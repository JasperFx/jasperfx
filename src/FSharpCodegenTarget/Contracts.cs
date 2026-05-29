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
