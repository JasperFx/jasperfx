using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
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
    [InlineData(typeof(OverridesAndUsesConventions),
        "Event projections can be written by either overriding the ApplyAsync() method or by using conventional methods and inline lambda registrations per event type, but not both")]
    public void bad_options(Type type, string message)
    {
        var ex = Should.Throw<InvalidProjectionException>(() =>
        {
            Activator.CreateInstance(type).As<EventProjection>().AssembleAndAssertValidity();
        });

        ex.Message.ShouldBe(message);
    }

    [Fact]
    public async Task apply_event_exception_wrapping()
    {
        ProjectionExceptions.RegisterTransientExceptionType<SpecialEventException>();

        var projection = new ErrorCausingProjection();

        await Should.ThrowAsync<SpecialEventException>(async () =>
        {
            await projection.As<IJasperFxProjection<FakeOperations>>()
                .ApplyAsync(new FakeOperations(), [new Event<AEvent>(new AEvent())], CancellationToken.None);
        });
        
        var ex = await Should.ThrowAsync<ApplyEventException>(async () =>
        {
            await projection.As<IJasperFxProjection<FakeOperations>>()
                .ApplyAsync(new FakeOperations(), [new Event<BEvent>(new BEvent())], CancellationToken.None);
        });

        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }
}

public class ErrorCausingProjection : EventProjection
{
    public void Project(FakeOperations operations, AEvent e)
    {
        throw new SpecialEventException("bang.");
    }

    public void Project(FakeOperations operations, BEvent e)
    {
        throw new InvalidOperationException("no good");
    }
}

public class SpecialEventException : Exception
{
    public SpecialEventException(string? message) : base(message)
    {
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
    protected override void storeEntity<T>(FakeOperations ops, T entity)
    {
        throw new NotImplementedException();
    }
}