using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;

namespace EventTests.Projections;

public class AggregateVersioningTests
{
    [Theory]
    [InlineData(typeof(ConventionalVersionedAggregate), "Version")]
    [InlineData(typeof(ConventionalVersionedAggregate2), "Version")]
    [InlineData(typeof(ConventionalVersionedAggregate3), "Version")]
    [InlineData(typeof(ConventionalVersionedAggregate4), "Version")]
    [InlineData(typeof(ConventionalVersionedAggregate5), null)]
    [InlineData(typeof(ConventionalVersionedAggregate6), "VersionOverride")]
    [InlineData(typeof(ConventionalVersionedAggregate6Field), "VersionOverride")]
    [InlineData(typeof(ConventionalVersionedAggregate7), null)]
    public void find_conventional_property_or_field(Type aggregateType, string expectedMemberName)
    {
        var versioning =
            typeof(AggregateVersioning<,>).CloseAndBuildAs<IAggregateVersioning>(
                AggregationScope.SingleStream, aggregateType, typeof(FakeSession));

        (versioning.VersionMember?.Name).ShouldBe(expectedMemberName);
    }

    [Fact]
    public void override_version_member_int()
    {
        var versioning = new AggregateVersioning<AggregateWithMultipleCandidates, FakeSession>(AggregationScope.SingleStream);
        versioning.Override(x => x.RealVersion);
        versioning.VersionMember.Name.ShouldBe(nameof(AggregateWithMultipleCandidates.RealVersion));
    }

    [Fact]
    public void override_version_member_long()
    {
        var versioning = new AggregateVersioning<AggregateWithMultipleCandidates, FakeSession>(AggregationScope.SingleStream);
        versioning.Override(x => x.LongVersion);
        versioning.VersionMember.Name.ShouldBe(nameof(AggregateWithMultipleCandidates.LongVersion));
    }

    public const long StreamVersion = 5;
    public const long SequenceVersion = 100;

    [Theory]
    [InlineData(typeof(ConventionalVersionedAggregate), AggregationScope.SingleStream, StreamVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate2), AggregationScope.SingleStream, StreamVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate3), AggregationScope.SingleStream, StreamVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate4), AggregationScope.SingleStream, StreamVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate6), AggregationScope.SingleStream, StreamVersion)]

    [InlineData(typeof(ConventionalVersionedAggregate), AggregationScope.MultiStream, SequenceVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate2), AggregationScope.MultiStream, SequenceVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate3), AggregationScope.MultiStream, SequenceVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate4), AggregationScope.MultiStream, SequenceVersion)]
    [InlineData(typeof(ConventionalVersionedAggregate6), AggregationScope.MultiStream, SequenceVersion)]
    public void set_version_single_stream_on_internal(Type aggregateType, AggregationScope scope, long expected)
    {
        var e = new Event<AEvent>(new AEvent()) { Sequence = SequenceVersion, Version = StreamVersion };

        var versioning =
            typeof(AggregateVersioning<,>).CloseAndBuildAs<IAggregateVersioning>(
                scope, aggregateType, typeof(FakeSession));
        var aggregate = Activator.CreateInstance(aggregateType);
        versioning.TrySetVersion(aggregate, e);


    }
}

public class FakeSession;

public class OrderShipped{}

public class AggregateWithMultipleCandidates
{
    public int Version { get; set; }
    public int RealVersion { get; set; }
    public long LongVersion { get; set; }
}

public class ConventionalVersionedAggregate
{
    internal int Version;
}

public class ConventionalVersionedAggregate2
{
    public int Version { get; set; }
}

public class ConventionalVersionedAggregate3
{
    public long Version;
}

public class ConventionalVersionedAggregate4
{
    internal long Version { get; set; }
}

public class ConventionalVersionedAggregate5
{
    // Should not catch
    public string Version { get; set; }
}

public class ConventionalVersionedAggregate6
{
    // Should not catch
    public int Version { get; set; }

    [Version]
    public int VersionOverride { get; set; }
}

public class ConventionalVersionedAggregate6Field
{
    // Should not catch
    public int Version { get; set; }

    [Version] public int VersionOverride;
}

public class ConventionalVersionedAggregate7
{
    [JasperFxIgnore]
    public int Version { get; set; }
}

public class VersionAttribute : Attribute, IVersionAttribute
{
    
}
