using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using Shouldly;

namespace EventTests.Projections;

// jasperfx#1241: a self-aggregating type with its own Evolve()/EvolveAsync() got a dispatcher that sent
// every event to that method and ignored the conventional Apply/Create/ShouldDelete methods it inherits
// from a base class, and the event filter left their event types out. The shape that motivated it is
// Wolverine's event-sourced saga: the framework base class applies its own events (compensation, completion)
// with conventional methods, and the user's saga is written with Evolve(). These types are compiled by
// the real generator and run through the runtime projection, so both halves have to agree.
public record Bug1241BaseHandled;
public record Bug1241Shared : IBug1241Marker;
public interface IBug1241Marker;
public record Bug1241Own;
public record Bug1241Removed;
public record Bug1241Unrelated;

public abstract class Bug1241WorkflowBase
{
    public List<string> Log { get; set; } = new();

    public void Apply(Bug1241BaseHandled e) => Log.Add("base:BaseHandled");
    public void Apply(Bug1241Shared e) => Log.Add("base:Shared");
}

// void Evolve(object): Shared is named by both, BaseHandled only by the base class.
public class Bug1241MutableObjectEvolve : Bug1241WorkflowBase
{
    public Guid Id { get; set; }

    public void Evolve(object e)
    {
        switch (e)
        {
            case Bug1241Own own:
                Log.Add("evolve:Own");
                break;
            case Bug1241Shared shared:
                Log.Add("evolve:Shared");
                break;
            default:
                // Never reached by BaseHandled: Evolve names its event types, and BaseHandled is not
                // one of them, so the inherited Apply alone handles it.
                Log.Add("evolve:default:" + e.GetType().Name);
                break;
        }
    }
}

// void Evolve(IEvent)
public class Bug1241MutableIEventEvolve : Bug1241WorkflowBase
{
    public Guid Id { get; set; }

    public void Evolve(IEvent e)
    {
        switch (e)
        {
            case IEvent<Bug1241Own> own:
                Log.Add("evolve:Own");
                break;
            case IEvent<Bug1241Shared> shared:
                Log.Add("evolve:Shared");
                break;
        }
    }
}

// TDoc Evolve(IEvent), immutable form: returns a new instance carrying the log forward.
public class Bug1241ImmutableEvolve : Bug1241WorkflowBase
{
    public Guid Id { get; set; }

    public Bug1241ImmutableEvolve Evolve(IEvent e)
    {
        var next = new Bug1241ImmutableEvolve { Id = Id, Log = new List<string>(Log) };
        switch (e)
        {
            case IEvent<Bug1241Own> own:
                next.Log.Add("evolve:Own");
                break;
            case IEvent<Bug1241Shared> shared:
                next.Log.Add("evolve:Shared");
                break;
        }

        return next;
    }
}

// Task EvolveAsync(object, CancellationToken), mutable async form.
public class Bug1241MutableAsyncEvolve : Bug1241WorkflowBase
{
    public Guid Id { get; set; }

    public async Task EvolveAsync(object e, CancellationToken token)
    {
        await Task.Yield();
        switch (e)
        {
            case Bug1241Own own:
                Log.Add("evolve:Own");
                break;
            case Bug1241Shared shared:
                Log.Add("evolve:Shared");
                break;
        }
    }
}

// ValueTask<TDoc> EvolveAsync(IEvent), immutable async form.
public class Bug1241ImmutableAsyncEvolve : Bug1241WorkflowBase
{
    public Guid Id { get; set; }

    public async ValueTask<Bug1241ImmutableAsyncEvolve> EvolveAsync(IEvent e)
    {
        await Task.Yield();
        var next = new Bug1241ImmutableAsyncEvolve { Id = Id, Log = new List<string>(Log) };
        switch (e)
        {
            case IEvent<Bug1241Own> own:
                next.Log.Add("evolve:Own");
                break;
            case IEvent<Bug1241Shared> shared:
                next.Log.Add("evolve:Shared");
                break;
        }

        return next;
    }
}

// Evolve names an interface the inherited event type implements, so it sees that event too.
public class Bug1241InterfaceEvolve : Bug1241WorkflowBase
{
    public Guid Id { get; set; }

    public void Evolve(object e)
    {
        if (e is Bug1241Own) Log.Add("evolve:Own");
        if (e is IBug1241Marker marker) Log.Add("evolve:Marker");
    }
}

// A catch-all Evolve names no event type, which means "no event filter": it already received every event,
// so it still does, after any inherited method for that event type.
public class Bug1241CatchAllEvolve : Bug1241WorkflowBase
{
    public Guid Id { get; set; }

    public void Evolve(object e) => Log.Add("evolve:" + e.GetType().Name.Replace("Bug1241", ""));
}

public abstract class Bug1241DeletingBase : Bug1241WorkflowBase
{
    public bool ShouldDelete(Bug1241Removed e) => true;
}

public class Bug1241EvolveWithInheritedShouldDelete : Bug1241DeletingBase
{
    public Guid Id { get; set; }

    public void Evolve(object e)
    {
        if (e is Bug1241Own) Log.Add("evolve:Own");
    }
}

public class Bug1241AsyncEvolveWithInheritedShouldDelete : Bug1241DeletingBase
{
    public Guid Id { get; set; }

    public Task EvolveAsync(object e)
    {
        if (e is Bug1241Own) Log.Add("evolve:Own");
        return Task.CompletedTask;
    }
}

public class Bug_1241_evolve_with_inherited_conventional_methods
{
    private static async Task<(TDoc?, ActionType)> run<TDoc>(params object[] events)
        where TDoc : Bug1241WorkflowBase
    {
        var projection = new SingleStreamProjection<TDoc, Guid>();
        projection.AssembleAndAssertValidity();

        return await projection.DetermineActionAsync(
            new FakeSession(),
            null,
            Guid.NewGuid(),
            new NulloIdentitySetter<TDoc, Guid>(),
            events.Select(x => (IEvent)Activator.CreateInstance(typeof(Event<>).MakeGenericType(x.GetType()), x)!)
                .ToList(),
            CancellationToken.None);
    }

    private static async Task<List<string>> logOf<TDoc>() where TDoc : Bug1241WorkflowBase
    {
        var (snapshot, action) = await run<TDoc>(new Bug1241BaseHandled(), new Bug1241Own(), new Bug1241Shared());

        action.ShouldBe(ActionType.Store);
        snapshot.ShouldNotBeNull();
        return snapshot.Log;
    }

    // The precedence rule: an inherited conventional method handles its event type; Evolve gets every
    // other event; when Evolve also names the event type (or one of its base types or interfaces), or names
    // none at all, the inherited method runs first, then Evolve.
    private static readonly string[] ExpectedLog =
        ["base:BaseHandled", "evolve:Own", "base:Shared", "evolve:Shared"];

    [Fact]
    public async Task mutable_object_evolve() =>
        (await logOf<Bug1241MutableObjectEvolve>()).ShouldBe(ExpectedLog);

    [Fact]
    public async Task mutable_ievent_evolve() =>
        (await logOf<Bug1241MutableIEventEvolve>()).ShouldBe(ExpectedLog);

    [Fact]
    public async Task immutable_evolve() =>
        (await logOf<Bug1241ImmutableEvolve>()).ShouldBe(ExpectedLog);

    [Fact]
    public async Task mutable_async_evolve() =>
        (await logOf<Bug1241MutableAsyncEvolve>()).ShouldBe(ExpectedLog);

    [Fact]
    public async Task immutable_async_evolve() =>
        (await logOf<Bug1241ImmutableAsyncEvolve>()).ShouldBe(ExpectedLog);

    [Fact]
    public async Task evolve_naming_an_interface_of_an_inherited_event_type_also_receives_it() =>
        (await logOf<Bug1241InterfaceEvolve>()).ShouldBe(
            ["base:BaseHandled", "evolve:Own", "base:Shared", "evolve:Marker"]);

    [Fact]
    public async Task a_catch_all_evolve_still_gets_every_event_after_the_inherited_method() =>
        (await logOf<Bug1241CatchAllEvolve>()).ShouldBe(
            ["base:BaseHandled", "evolve:BaseHandled", "evolve:Own", "base:Shared", "evolve:Shared"]);

    [Theory]
    [InlineData(typeof(Bug1241MutableObjectEvolve))]
    [InlineData(typeof(Bug1241MutableIEventEvolve))]
    [InlineData(typeof(Bug1241ImmutableEvolve))]
    [InlineData(typeof(Bug1241MutableAsyncEvolve))]
    [InlineData(typeof(Bug1241ImmutableAsyncEvolve))]
    public void inherited_event_types_join_the_event_filter(Type aggregateType)
    {
        var projection = (JasperFx.Events.Projections.ProjectionBase)Activator.CreateInstance(
            typeof(SingleStreamProjection<,>).MakeGenericType(aggregateType, typeof(Guid)))!;
        projection.AssembleAndAssertValidity();

        var eventTypes = ((IAggregateProjection)projection).AllEventTypes;
        eventTypes.ShouldContain(typeof(Bug1241Own));
        eventTypes.ShouldContain(typeof(Bug1241BaseHandled));
        eventTypes.ShouldContain(typeof(Bug1241Shared));
        eventTypes.ShouldNotContain(typeof(Bug1241Unrelated));
    }

    [Fact]
    public void a_catch_all_evolve_keeps_an_empty_event_filter_despite_inherited_methods()
    {
        // An empty filter is how a catch-all aggregation says "every event". Adding the inherited types
        // would quietly narrow it to exactly those, and the Evolve would never see anything else.
        var projection = new SingleStreamProjection<Bug1241CatchAllEvolve, Guid>();
        projection.AssembleAndAssertValidity();

        projection.AllEventTypes.ShouldBeEmpty();
        projection.AppliesTo([typeof(Bug1241Unrelated)]).ShouldBeTrue();
    }

    [Fact]
    public async Task an_inherited_should_delete_deletes_the_aggregate()
    {
        var (snapshot, action) = await run<Bug1241EvolveWithInheritedShouldDelete>(
            new Bug1241Own(), new Bug1241Removed());

        snapshot.ShouldBeNull();
        action.ShouldBe(ActionType.Nothing); // nothing existed before the batch

        var projection = new SingleStreamProjection<Bug1241EvolveWithInheritedShouldDelete, Guid>();
        projection.AssembleAndAssertValidity();
        var existing = new Bug1241EvolveWithInheritedShouldDelete();
        var (afterDelete, deleteAction) = await projection.DetermineActionAsync(new FakeSession(), existing,
            Guid.NewGuid(), new NulloIdentitySetter<Bug1241EvolveWithInheritedShouldDelete, Guid>(),
            [new Event<Bug1241Removed>(new Bug1241Removed())], CancellationToken.None);

        afterDelete.ShouldBeNull();
        deleteAction.ShouldBe(ActionType.Delete);
        projection.AllEventTypes.ShouldContain(typeof(Bug1241Removed));
    }

    [Fact]
    public async Task an_inherited_should_delete_with_an_async_evolve_deletes_the_aggregate()
    {
        var projection = new SingleStreamProjection<Bug1241AsyncEvolveWithInheritedShouldDelete, Guid>();
        projection.AssembleAndAssertValidity();

        var existing = new Bug1241AsyncEvolveWithInheritedShouldDelete();
        var (kept, keptAction) = await projection.DetermineActionAsync(new FakeSession(), existing,
            Guid.NewGuid(), new NulloIdentitySetter<Bug1241AsyncEvolveWithInheritedShouldDelete, Guid>(),
            [new Event<Bug1241Own>(new Bug1241Own()), new Event<Bug1241BaseHandled>(new Bug1241BaseHandled())],
            CancellationToken.None);

        keptAction.ShouldBe(ActionType.Store);
        kept!.Log.ShouldBe(["evolve:Own", "base:BaseHandled"]);

        var (deleted, deleteAction) = await projection.DetermineActionAsync(new FakeSession(), kept,
            Guid.NewGuid(), new NulloIdentitySetter<Bug1241AsyncEvolveWithInheritedShouldDelete, Guid>(),
            [new Event<Bug1241Removed>(new Bug1241Removed())], CancellationToken.None);

        deleted.ShouldBeNull();
        deleteAction.ShouldBe(ActionType.Delete);
    }
}
