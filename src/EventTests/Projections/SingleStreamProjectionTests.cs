using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class SingleStreamProjectionTests
{
    [Theory]
    [InlineData("Only using conventional methods", typeof(ConventionalProjection))]
    [InlineData("Overrides DetermineAction()", typeof(OverridesDetermineAction))]
    [InlineData("Overrides DetermineActionAsync()", typeof(OverridesDetermineActionAsync))]
    [InlineData("Overrides Evolve()", typeof(OverridesEvolve))]
    [InlineData("Overrides EvolveAsync()", typeof(OverridesEvolveAsync))]
    [InlineData("Uses inline lambdas", typeof(InlineProjection))]
    public void validation_is_good_with_only_conventional_methods(string explanation, Type type)
    {
        Activator.CreateInstance(type).As<ProjectionBase>().AssembleAndAssertValidity();
    }

    [Theory]
    [InlineData(typeof(ConventionalPlusEvolve), "This projection can only use the override of 'Evolve' or conventional Apply/Create/ShouldDelete methods and line lambdas, but not both")]
    [InlineData(typeof(MultipleOverrides), "Only one of these methods can be overridden: Evolve, EvolveAsync")]
    [InlineData(typeof(EmptyProjection), "No matching conventional Apply/Create methods for the EventTests.MyAggregate aggregate.")]
    public void validation_fails(Type type, string message)
    {
        var ex = Should.Throw<InvalidProjectionException>(() =>
        {
            Activator.CreateInstance(type).As<ProjectionBase>().AssembleAndAssertValidity();
        });
        
        ex.Message.ShouldBe(message);
    }

}

public class EmptyProjection : SingleStreamProjection<MyAggregate, Guid>
{
    
}

public class ConventionalPlusEvolve : SingleStreamProjection<MyAggregate, Guid>
{
    public void Apply(AEvent e, MyAggregate a) => a.ACount++;

    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        return base.Evolve(snapshot, id, e);
    }
}

public class MultipleOverrides : SingleStreamProjection<MyAggregate, Guid>
{
    public override ValueTask<MyAggregate?> EvolveAsync(MyAggregate? snapshot, Guid id, FakeSession session, IEvent e, CancellationToken cancellation)
    {
        return base.EvolveAsync(snapshot, id, session, e, cancellation);
    }

    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        return base.Evolve(snapshot, id, e);
    }
}

public class ConventionalProjection : SingleStreamProjection<MyAggregate, Guid>
{
    public void Apply(AEvent e, MyAggregate a) => a.ACount++;
}

public class InlineProjection : SingleStreamProjection<MyAggregate, Guid>
{
    public InlineProjection()
    {
        ProjectEvent<AEvent>((a, e) => a.ACount++);
    }
}

public class OverridesDetermineAction : SingleStreamProjection<MyAggregate, Guid>
{
    public override (MyAggregate?, ActionType) DetermineAction(MyAggregate? snapshot, Guid identity, IReadOnlyList<IEvent> events)
    {
        throw new NotImplementedException();
    }
}

public class OverridesDetermineActionAsync : SingleStreamProjection<MyAggregate, Guid>
{
    public override ValueTask<(MyAggregate?, ActionType)> DetermineActionAsync(FakeSession session, MyAggregate? snapshot, Guid identity,
        IIdentitySetter<MyAggregate, Guid> identitySetter, IReadOnlyList<IEvent> events, CancellationToken cancellation)
    {
        throw new NotImplementedException();
    }
}

public class OverridesEvolve : SingleStreamProjection<MyAggregate, Guid>
{
    public override MyAggregate? Evolve(MyAggregate? snapshot, Guid id, IEvent e)
    {
        throw new NotImplementedException();
    }
}

public class OverridesEvolveAsync : SingleStreamProjection<MyAggregate, Guid>
{
    public override ValueTask<MyAggregate?> EvolveAsync(MyAggregate? snapshot, Guid id, FakeSession session, IEvent e, CancellationToken cancellation)
    {
        throw new NotImplementedException();
    }
}

public abstract class SingleStreamProjection<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, FakeOperations, FakeSession>
{
    protected SingleStreamProjection() : base([typeof(BadImageFormatException), typeof(DivideByZeroException)])
    {
    }
}

public class FakeSingleProjectionStream<TDoc, TId> : JasperFxSingleStreamProjectionBase<TDoc, TId, FakeOperations, FakeSession>
{
    public FakeSingleProjectionStream(Type[] transientExceptionTypes) : base(transientExceptionTypes)
    {
    }
}

