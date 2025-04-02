using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class EventProjectionTests
{
    [Theory]
    [InlineData(typeof(LambdaEventProjection))]
    [InlineData(typeof(ConventionalEventProjection))]
    [InlineData(typeof(OverridesApplyAsyncEventProjection))]
    public void good_options(Type type)
    {
        Activator.CreateInstance(type).As<EventProjection>().AssembleAndAssertValidity();
    }

    [Theory]
    [InlineData(typeof(OverridesAndUsesConventions), "Event projections can be written by either overriding the ApplyAsync() method or by using conventional methods and inline lambda registrations per event type, but not both")]
    public void bad_options(Type type, string message)
    {
        var ex = Should.Throw<InvalidProjectionException>(() =>
        {
            Activator.CreateInstance(type).As<EventProjection>().AssembleAndAssertValidity();
        });
        
        ex.Message.ShouldBe(message);
    }
}

public class EmptyEventProjection : EventProjection
{
    
}

public class LambdaEventProjection : EventProjection
{
    public LambdaEventProjection()
    {
        Project<AEvent>((e, o) => { });
    }
}

public class ConventionalEventProjection : EventProjection
{
    public void Project(AEvent e, FakeOperations ops)
    {
        // nothing
    }
}

public class OverridesApplyAsyncEventProjection : EventProjection
{
    public override ValueTask ApplyAsync(FakeOperations operations, IEvent e, CancellationToken cancellation)
    {
        return base.ApplyAsync(operations, e, cancellation);
    }
}

public class OverridesAndUsesConventions : EventProjection
{
    public override ValueTask ApplyAsync(FakeOperations operations, IEvent e, CancellationToken cancellation)
    {
        return base.ApplyAsync(operations, e, cancellation);
    }
    
    public void Project(AEvent e, FakeOperations ops)
    {
        // nothing
    }
}

public class EventProjection : JasperFxEventProjectionBase<FakeOperations, FakeSession>
{
    public EventProjection() : base([])
    {
    }

    protected override void storeEntity<T>(FakeOperations ops, T entity)
    {
        throw new NotImplementedException();
    }
}