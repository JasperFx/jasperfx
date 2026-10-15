using System.Data.Common;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class ProjectionExceptionsTests
{
    [Fact]
    public void is_transient_exception()
    {
        ProjectionExceptions.RegisterTransientExceptionType<DatabaseException>();
        ProjectionExceptions.RegisterTransientExceptionType<DivideByZeroException>();
        
        ProjectionExceptions.IsExceptionTransient(new DatabaseException()).ShouldBeTrue();
        ProjectionExceptions.IsExceptionTransient(new DivideByZeroException()).ShouldBeTrue();
        ProjectionExceptions.IsExceptionTransient(new BadImageFormatException()).ShouldBeFalse();
        ProjectionExceptions.IsExceptionTransient(new InvalidOperationException()).ShouldBeFalse();
        ProjectionExceptions.IsExceptionTransient(new InvalidEventToStartAggregateException(typeof(SimpleAggregate), typeof(SimpleAggregate), typeof(SimpleAggregate))).ShouldBeFalse();
    }

    // jasperfx#1019: Polecat and Fisher register no exception types, only a TransientFailures classifier
    private sealed class DriverException(bool transient) : DbException("driver failure")
    {
        public override bool IsTransient => transient;
    }

    private sealed class StoreCommandException(Exception inner) : Exception("store command failed", inner);

    [Fact]
    public void a_driver_exception_reporting_itself_transient_is_transient_without_registration()
    {
        // A SQL Server deadlock or SQLITE_BUSY: retry, never dead-letter
        ProjectionExceptions.IsExceptionTransient(new DriverException(true)).ShouldBeTrue();
    }

    [Fact]
    public void a_permanent_driver_exception_is_not_transient()
    {
        // A missing column: fail fast, skippable with SkipApplyErrors
        ProjectionExceptions.IsExceptionTransient(new DriverException(false)).ShouldBeFalse();
    }

    [Fact]
    public void a_transient_fault_wrapped_by_the_store_is_transient()
    {
        ProjectionExceptions.IsExceptionTransient(new StoreCommandException(new DriverException(true))).ShouldBeTrue();
        ProjectionExceptions.IsExceptionTransient(new StoreCommandException(new DriverException(false))).ShouldBeFalse();
    }

    [Fact]
    public void timeouts_are_transient()
    {
        ProjectionExceptions.IsExceptionTransient(new TimeoutException()).ShouldBeTrue();
    }

    [Fact]
    public void a_store_classifier_is_consulted()
    {
        TransientFailures.AddClassifier(ClassifiedException.Classify);
        ProjectionExceptions.IsExceptionTransient(new ClassifiedException()).ShouldBeTrue();
    }

    [Fact]
    public void an_apply_event_exception_is_never_transient()
    {
        var e = new Event<AEvent>(new AEvent()) { Id = Guid.NewGuid(), Sequence = 1, StreamId = Guid.NewGuid() };
        ProjectionExceptions.IsExceptionTransient(new ApplyEventException(e, new DriverException(true))).ShouldBeFalse();
    }
}

public class ClassifiedException : Exception
{
    public static bool? Classify(Exception e) => e is ClassifiedException ? true : null;
}

public class DatabaseException : Exception{}
