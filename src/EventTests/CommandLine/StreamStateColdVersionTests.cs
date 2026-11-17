using JasperFx.Events;
using JasperFx.Events.CommandLine;
using Shouldly;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#1211 — <see cref="StreamState.ColdVersion"/>, a stream's cold boundary. Null unless a store with a
/// cold event store (Marten's, marten#5710) fills it in, and carried through to the stream-query report.
/// </summary>
public class StreamStateColdVersionTests
{
    [Fact]
    public void a_stream_has_no_cold_version_by_default()
    {
        new StreamState().ColdVersion.ShouldBeNull();
        new StreamState(Guid.NewGuid(), 3, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).ColdVersion.ShouldBeNull();
        new StreamState("key", 3, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).ColdVersion.ShouldBeNull();
    }

    [Fact]
    public void the_stream_query_report_carries_the_cold_version()
    {
        var state = new StreamState("partly-cold", 45, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            ColdVersion = 40
        };

        StreamQueryStreamReport.From(state).ColdVersion.ShouldBe(40);
    }

    [Fact]
    public void the_stream_query_report_has_no_cold_version_for_a_stream_without_one()
    {
        var state = new StreamState("all-warm", 45, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        StreamQueryStreamReport.From(state).ColdVersion.ShouldBeNull();
    }
}
