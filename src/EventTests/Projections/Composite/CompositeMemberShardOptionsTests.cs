using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections.Composite;

// A composite runs as one shard built from its own AsyncOptions, so the shard-level settings on a member's
// options are never read. Setting one there must not pass in silence.
public class CompositeMemberShardOptionsTests
{
    private readonly IEventStore<FakeOperations, FakeSession> theStore =
        Substitute.For<IEventStore<FakeOperations, FakeSession>>();

    private readonly IEventDatabase theDatabase = Substitute.For<IEventDatabase>();
    private readonly CapturingLogger theLogger = new();

    private void buildExecution(CompositeProjection<FakeOperations, FakeSession> composite, string? tenantId = null)
    {
        ((ISubscriptionFactory<FakeOperations, FakeSession>)composite).BuildExecution(theStore, theDatabase,
            theLogger, ShardName.Compose(composite.Name, tenantId: tenantId, version: composite.Version));
    }

    public static TheoryData<string, Action<AsyncOptions>> ShardLevelSettings => new()
    {
        { nameof(AsyncOptions.BatchSize), o => o.BatchSize = 50 },
        { nameof(AsyncOptions.MaximumHopperSize), o => o.MaximumHopperSize = 2000 },
        { nameof(AsyncOptions.EnableDocumentTrackingByIdentity), o => o.EnableDocumentTrackingByIdentity = true },
        { nameof(AsyncOptions.GateSideEffectsBehindPriorVersion), o => o.GateSideEffectsBehindPriorVersion = false },
        { "SubscribeFrom", o => o.SubscribeFromPresent() }
    };

    [Theory]
    [MemberData(nameof(ShardLevelSettings))]
    public void a_shard_level_setting_on_a_member_is_reported(string setting, Action<AsyncOptions> configure)
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        var member = new FakeReplayMember("Member", canParticipate: true);
        configure(member.Options);
        composite.StageFor(1).Add(member);

        buildExecution(composite);

        var warning = theLogger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("'Member'");
        warning.ShouldContain("'Composite'");
        warning.ShouldContain(setting);
    }

    [Fact]
    public void cache_limit_per_tenant_on_a_member_is_not_reported_because_the_member_reads_it()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        var member = new FakeReplayMember("Member", canParticipate: true);
        member.Options.CacheLimitPerTenant = 1000;
        composite.StageFor(1).Add(member);

        buildExecution(composite);

        theLogger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void shard_level_settings_on_the_composite_itself_are_not_reported()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        composite.Options.BatchSize = 50;
        composite.Options.EnableDocumentTrackingByIdentity = true;
        composite.StageFor(1).Add(new FakeReplayMember("Member", canParticipate: true));

        buildExecution(composite);

        theLogger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void every_ignored_setting_of_a_member_is_listed_in_one_warning()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        var member = new FakeReplayMember("Member", canParticipate: true);
        member.Options.BatchSize = 50;
        member.Options.EnableDocumentTrackingByIdentity = true;
        composite.StageFor(1).Add(member);

        buildExecution(composite);

        var warning = theLogger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain(nameof(AsyncOptions.BatchSize));
        warning.ShouldContain(nameof(AsyncOptions.EnableDocumentTrackingByIdentity));
    }

    [Fact]
    public void the_member_of_a_nested_composite_is_reported_too()
    {
        var outer = new CompositeProjection<FakeOperations, FakeSession>("Outer");
        var inner = new CompositeProjection<FakeOperations, FakeSession>("Inner");
        var member = new FakeReplayMember("Member", canParticipate: true);
        member.Options.BatchSize = 50;
        inner.StageFor(1).Add(member);
        inner.Options.BatchSize = 100;
        outer.StageFor(1).Add(inner);

        buildExecution(outer);

        theLogger.Warnings.Count.ShouldBe(2);
        theLogger.Warnings.ShouldContain(x => x.Contains("'Inner'") && x.Contains("'Outer'"));
        theLogger.Warnings.ShouldContain(x => x.Contains("'Member'") && x.Contains("'Inner'"));
    }

    [Fact]
    public void it_is_reported_once_however_many_shards_the_composite_runs()
    {
        var composite = new CompositeProjection<FakeOperations, FakeSession>("Composite");
        var member = new FakeReplayMember("Member", canParticipate: true);
        member.Options.BatchSize = 50;
        composite.StageFor(1).Add(member);

        buildExecution(composite, "tenant1");
        buildExecution(composite, "tenant2");
        buildExecution(composite, "tenant3");

        theLogger.Warnings.ShouldHaveSingleItem();
    }

    private class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
