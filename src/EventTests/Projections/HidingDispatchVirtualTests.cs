using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

// #656: declaring a dispatch method without `override` hides the base virtual rather than replacing
// it — the compiler says so with CS0114 — but isOverridden used to count it as an override purely
// because it is declared outside JasperFx.Events. With conventional methods alongside, registration
// failed claiming a conflict the author never wrote; without them, dispatch reached the base virtual
// and threw NotImplementedException at the first event.
public partial class HidesEvolveAndUsesConventions : SingleStreamProjection<MyAggregate, Guid>
{
    public new MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        // Never reached by dispatch — it is not an override. Kept as the author wrote it.
        snapshot ??= new MyAggregate();
        snapshot.ACount += 100;
        return snapshot;
    }

    public void Apply(AEvent e, MyAggregate a) => a.ACount++;
}

// A same-named helper used to be enough to break registration outright: Type.GetMethod(string) throws
// AmbiguousMatchException as soon as more than one method carries the name.
public partial class OverloadsEvolveName : SingleStreamProjection<MyAggregate, Guid>
{
    public string Evolve(string somethingElse) => somethingElse;

    public void Apply(AEvent e, MyAggregate a) => a.ACount++;
}

public class OverridesEvolveWithConventions : SingleStreamProjection<MyAggregate, Guid>
{
    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        snapshot ??= new MyAggregate();
        snapshot.ACount += 100;
        return snapshot;
    }
}

public class hiding_a_dispatch_virtual
{
    [Fact]
    public async Task a_hiding_evolve_does_not_claim_the_dispatch_path()
    {
        var projection = new HidesEvolveAndUsesConventions();

        // Used to throw: "can only use the override of 'Evolve' or conventional Apply/Create/
        // ShouldDelete methods, but not both".
        Should.NotThrow(() => projection.AssembleAndAssertValidity());

        var (snapshot, _) = await projection.DetermineActionAsync(
            new FakeSession(),
            new MyAggregate(),
            Guid.NewGuid(),
            new NulloIdentitySetter<MyAggregate, Guid>(),
            [new Event<AEvent>(new AEvent()) { Sequence = 1 }],
            CancellationToken.None);

        // The generated dispatcher ran the conventional Apply — not the hiding method, which would
        // have added 100.
        snapshot!.ACount.ShouldBe(1);
    }

    [Fact]
    public void a_same_named_helper_does_not_break_registration()
    {
        var projection = new OverloadsEvolveName();

        // Used to throw AmbiguousMatchException out of Type.GetMethod("Evolve").
        Should.NotThrow(() => projection.AssembleAndAssertValidity());
    }

    [Fact]
    public async Task a_real_override_still_owns_dispatch()
    {
        // The other half of the contract: a genuine override must keep winning over both the
        // generated evolver and the conventional methods.
        var projection = new OverridesEvolveWithConventions();
        projection.AssembleAndAssertValidity();

        var (snapshot, _) = await projection.DetermineActionAsync(
            new FakeSession(),
            new MyAggregate(),
            Guid.NewGuid(),
            new NulloIdentitySetter<MyAggregate, Guid>(),
            [new Event<AEvent>(new AEvent()) { Sequence = 1 }],
            CancellationToken.None);

        snapshot!.ACount.ShouldBe(100);
    }
}
