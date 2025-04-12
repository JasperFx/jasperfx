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
}

public class DatabaseException : Exception{}