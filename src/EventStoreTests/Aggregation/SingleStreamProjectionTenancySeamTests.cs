using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Shouldly;

namespace EventStoreTests.Aggregation;

/// <summary>
/// jasperfx#723: JasperFxSingleStreamProjectionBase resolves ForceSingleTenancy itself, from
/// <see cref="IEventTenancySource" /> on the session, rather than leaving it to each store's subclass.
/// </summary>
/// <remarks>
/// The point of the seam is that a store which has not opted in is left exactly where it was, so the
/// negative case here carries as much weight as the positive one.
/// </remarks>
public class SingleStreamProjectionTenancySeamTests
{
    private static bool ForcesSingleTenancyFor(IProbeSession session)
    {
        var slicer = new ProbeProjection().BuildSlicer(session);
        return slicer.ShouldBeOfType<TenantedEventSlicer<SimpleAggregate, Guid>>().ForceSingleTenancy;
    }

    [Fact]
    public void forces_single_tenancy_when_the_session_reports_a_single_tenanted_event_store()
    {
        ForcesSingleTenancyFor(new SingleTenantedProbeSession()).ShouldBeTrue();
    }

    [Fact]
    public void does_not_force_single_tenancy_when_the_event_store_is_conjoined()
    {
        ForcesSingleTenancyFor(new ConjoinedProbeSession()).ShouldBeFalse();
    }

    [Fact]
    public void a_session_that_has_not_adopted_the_seam_keeps_the_pre_723_behavior()
    {
        // Not implementing IEventTenancySource must be safe: this is every store on the release that
        // introduces the seam, so anything other than false here would be a silent behavior change
        // shipped to stores that did nothing.
        ForcesSingleTenancyFor(new PlainProbeSession()).ShouldBeFalse();
    }
}

public interface IProbeSession;

public class PlainProbeSession : IProbeSession;

public class SingleTenantedProbeSession : IProbeSession, IEventTenancySource
{
    public TenancyStyle EventTenancyStyle => TenancyStyle.Single;
}

public class ConjoinedProbeSession : IProbeSession, IEventTenancySource
{
    public TenancyStyle EventTenancyStyle => TenancyStyle.Conjoined;
}

public class ProbeOperations : IProbeSession, IStorageOperations
{
    public Task<IProjectionStorage<TDoc, TId>> FetchProjectionStorageAsync<TDoc, TId>(string tenantId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public bool EnableSideEffectsOnInlineProjections => false;

    public ValueTask<IMessageSink> GetOrStartMessageSink() => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class ProbeProjection : JasperFxSingleStreamProjectionBase<SimpleAggregate, Guid, ProbeOperations, IProbeSession>;
