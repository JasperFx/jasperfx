using System.Data.Common;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace EventTests.Daemon;

// jasperfx#507: the teardown catch clauses used to be `catch when
// (_cancellation.IsCancellationRequested)`, which discarded ANY exception type once the shard
// started stopping. Only genuine cancellation side effects may be discarded silently; anything
// else must at least be logged
public class CancellationExceptionsTests
{
    [Fact]
    public void plain_operation_canceled_is_cancellation_like()
    {
        CancellationExceptions.IsCancellationLike(new OperationCanceledException()).ShouldBeTrue();
        CancellationExceptions.IsCancellationLike(new TaskCanceledException()).ShouldBeTrue();
    }

    [Fact]
    public void wrapped_cancellation_is_cancellation_like()
    {
        var wrapped = new InvalidOperationException("outer", new OperationCanceledException());
        CancellationExceptions.IsCancellationLike(wrapped).ShouldBeTrue();
    }

    [Fact]
    public void aggregated_cancellation_is_cancellation_like()
    {
        var aggregate = new AggregateException(new OperationCanceledException(), new TaskCanceledException());
        CancellationExceptions.IsCancellationLike(aggregate).ShouldBeTrue();
    }

    [Fact]
    public void aggregate_containing_a_real_failure_is_not_cancellation_like()
    {
        var aggregate = new AggregateException(new OperationCanceledException(),
            new FakeDbException("23514"));
        CancellationExceptions.IsCancellationLike(aggregate).ShouldBeFalse();
    }

    [Fact]
    public void object_disposed_is_cancellation_like()
    {
        CancellationExceptions.IsCancellationLike(new ObjectDisposedException("NpgsqlConnection"))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("57014", true)] // query_canceled
    [InlineData("08006", true)] // connection_failure
    [InlineData("08003", true)] // connection_does_not_exist
    [InlineData("23514", false)] // check_violation -- the incident's missing-partition failure
    [InlineData("42P01", false)] // undefined_table
    [InlineData(null, false)]
    public void db_exceptions_classify_by_sql_state(string? sqlState, bool expected)
    {
        CancellationExceptions.IsCancellationLike(new FakeDbException(sqlState)).ShouldBe(expected);
    }

    [Fact]
    public void wrapped_db_exception_classifies_by_the_inner_sql_state()
    {
        // e.g. MartenCommandException wrapping the provider exception
        var cancelledUnderneath = new InvalidOperationException("command failed", new FakeDbException("57014"));
        CancellationExceptions.IsCancellationLike(cancelledUnderneath).ShouldBeTrue();

        var schemaProblemUnderneath = new InvalidOperationException("command failed", new FakeDbException("23514"));
        CancellationExceptions.IsCancellationLike(schemaProblemUnderneath).ShouldBeFalse();
    }

    [Fact]
    public void arbitrary_exceptions_are_not_cancellation_like()
    {
        CancellationExceptions.IsCancellationLike(new DivideByZeroException()).ShouldBeFalse();
        CancellationExceptions.IsCancellationLike(new InvalidOperationException()).ShouldBeFalse();
    }
}

public class GroupedProjectionExecutionTeardownTests
{
    private readonly RecordingLogger theLogger = new();
    private readonly IGroupedProjectionRunner theRunner = Substitute.For<IGroupedProjectionRunner>();
    private readonly ISubscriptionAgent theAgent = Substitute.For<ISubscriptionAgent>();

    public GroupedProjectionExecutionTeardownTests()
    {
        theAgent.Metrics.Returns(Substitute.For<ISubscriptionMetrics>());
        theRunner.SliceBehavior.Returns(SliceBehavior.JustInTime);
        theRunner.ErrorHandlingOptions(Arg.Any<ShardExecutionMode>()).Returns(new ErrorHandlingOptions());
    }

    private async Task<RecordingLogger> executeRangeThatFailsDuringTeardownAsync(Exception failure)
    {
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IProjectionBatch> buildThenFailAsync()
        {
            buildStarted.TrySetResult();
            await releaseBuild.Task;
            throw failure;
        }

        theRunner.BuildBatchAsync(Arg.Any<EventRange>(), Arg.Any<ShardExecutionMode>(), Arg.Any<CancellationToken>())
            .Returns(_ => buildThenFailAsync());

        var execution = new GroupedProjectionExecution(new ShardName("Fake"), theRunner, theLogger);

        var page = new EventPage(0);
        page.CalculateCeiling(500, 10);
        await execution.EnqueueAsync(page, theAgent);

        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Teardown starts while the batch build is in flight -- the exact window jasperfx#507
        // is about
        await execution.HardStopAsync();
        releaseBuild.SetResult();

        // The failure surfaces asynchronously on the block's consumer; wait for the first log
        // entry carrying it (buildBatchAsync logs at Error before rethrowing into the teardown
        // clause), then give the same-continuation teardown clause a beat to write its own entry
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (theLogger.Entries)
            {
                if (theLogger.Entries.Any(x => ReferenceEquals(x.Exception, failure)))
                {
                    break;
                }
            }

            await Task.Delay(25);
        }

        await Task.Delay(250);

        return theLogger;
    }

    [Fact]
    public async Task a_real_database_failure_during_teardown_is_logged_not_discarded()
    {
        var schemaProblem = new FakeDbException("23514");

        var logger = await executeRangeThatFailsDuringTeardownAsync(schemaProblem);

        // A 23514 is a schema problem regardless of the CTS state (jasperfx#507). buildBatchAsync
        // logs it at Error on the way out; the teardown clause records it again instead of
        // discarding it into the void
        logger.Entries.Any(x =>
                x.Level == LogLevel.Information &&
                ReferenceEquals(x.Exception, schemaProblem) &&
                x.Message.Contains("does not look like a cancellation side effect"))
            .ShouldBeTrue(
                $"expected an Information entry for the discarded schema problem, got: {logger.Describe()}");

        // But teardown must still not promote the failure to a critical shard failure
        await theAgent.DidNotReceive().ReportCriticalFailureAsync(Arg.Any<Exception>());
    }

    [Fact]
    public async Task a_genuine_cancellation_during_teardown_stays_silent()
    {
        var cancellation = new OperationCanceledException();

        var logger = await executeRangeThatFailsDuringTeardownAsync(cancellation);

        logger.Entries.Any(x => x.Message.Contains("does not look like a cancellation side effect"))
            .ShouldBeFalse($"a pure cancellation must not be logged as a discarded failure, got: {logger.Describe()}");

        await theAgent.DidNotReceive().ReportCriticalFailureAsync(Arg.Any<Exception>());
    }
}

public class FakeDbException : DbException
{
    public FakeDbException(string? sqlState)
    {
        SqlState = sqlState;
    }

    public override string? SqlState { get; }
}

public class RecordingLogger : ILogger
{
    public record Entry(LogLevel Level, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = new();

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public string Describe()
    {
        lock (Entries)
        {
            return string.Join("\n", Entries.Select(x => $"{x.Level}: {x.Message} ({x.Exception?.GetType().Name})"));
        }
    }
}
