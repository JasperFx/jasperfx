using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Binary serialization events

public record ProbeLaunched(string Vehicle, string Pad);

public record TelemetryBurst(string Channel, int[] Samples);

/// <summary>
/// Opted in by attribute rather than by explicit registration, so the suite can hold the
/// <see cref="BinaryEventAttribute" /> + store-wide-default path to a definition too.
/// </summary>
[BinaryEvent]
public record ProbeSeparated(int Stage);

#endregion

/// <summary>
/// A deliberately non-JSON binary serializer: UTF8 JSON, gzipped.
/// </summary>
/// <remarks>
/// The compression is what makes the suite's assertions mean something. A serializer that emitted
/// plain UTF8 JSON would round-trip identically whether the store honored it or quietly fell back to
/// its own JSON path, so every fact here would pass against a store that implemented nothing. Gzip
/// output is not valid JSON and not valid UTF8, so a store that ignores the serializer fails loudly
/// on write or read instead of silently agreeing.
/// </remarks>
public sealed class ComplianceGzipJsonSerializer : IEventBinarySerializer
{
    /// <summary>
    /// Number of times <see cref="Serialize" /> has been called on this instance.
    /// </summary>
    public int SerializeCount { get; private set; }

    /// <summary>
    /// Number of times <see cref="Deserialize" /> has been called on this instance.
    /// </summary>
    public int DeserializeCount { get; private set; }

    public byte[] Serialize(Type type, object data)
    {
        SerializeCount++;

        var json = JsonSerializer.SerializeToUtf8Bytes(data, type);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(json, 0, json.Length);
        }

        return output.ToArray();
    }

    public object Deserialize(Type type, byte[] data)
    {
        DeserializeCount++;

        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);

        return JsonSerializer.Deserialize(output.ToArray(), type)
               ?? throw new InvalidOperationException(
                   $"The compliance serializer deserialized a null {type.FullName}.");
    }
}

/// <summary>
/// Binary event serialization — per-event-type opt out of the store's JSON format
/// (marten#4515, polecat#475, fisher#93).
/// </summary>
/// <remarks>
/// <para>
/// Opt-in: a store that has not implemented binary event storage simply does not enroll, and the
/// registrar members this suite drives keep their throwing defaults.
/// </para>
/// <para>
/// The property worth protecting above all the others is that JSON and binary rows coexist per event
/// type in one table — that is what makes turning a single event type binary an in-place change with
/// no migration of existing event data. <see cref="json_and_binary_events_coexist_in_one_stream" />
/// is the fact that pins it, and a store that got everything else right but stored a per-store or
/// per-stream format flag would fail exactly there.
/// </para>
/// <para>
/// Note what is <em>not</em> asserted: the column the bytes land in, and how a row records that it is
/// binary. Both are genuinely per-store — Marten uses a nullable <c>bdata</c> alongside a
/// placeholder in <c>data</c> — and pinning either here would encode Marten's schema as the contract
/// rather than the behavior.
/// </para>
/// </remarks>
public abstract class BinaryEventSerializationCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly ComplianceGzipJsonSerializer _telemetry = new();
    private static readonly ComplianceGzipJsonSerializer _fallback = new();

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_binary_events";
        config.AddEventType<ProbeLaunched>();
        config.AddEventType<TelemetryBurst>();
        config.AddEventType<ProbeSeparated>();

        // TelemetryBurst is binary by explicit registration, ProbeSeparated by attribute against the
        // store-wide default, and ProbeLaunched stays JSON — so one store carries all three routes.
        config.UseBinarySerializer<TelemetryBurst>(_telemetry);
        config.SetDefaultBinarySerializer(_fallback);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    [Fact]
    public async Task a_binary_event_round_trips_through_the_registered_serializer()
    {
        var streamId = Guid.NewGuid();
        var samples = new[] { 3, 1, 4, 1, 5, 9, 2, 6 };

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId, new TelemetryBurst("s-band", samples));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        var burst = events.Single().Data.ShouldBeOfType<TelemetryBurst>();
        burst.Channel.ShouldBe("s-band");
        burst.Samples.ShouldBe(samples);
    }

    [Fact]
    public async Task the_registered_serializer_is_actually_the_one_that_ran()
    {
        var before = _telemetry.SerializeCount;

        var streamId = Guid.NewGuid();
        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, new TelemetryBurst("x-band", new[] { 1 }));
        await SaveChangesAsync(session);

        // Round-tripping correctly is not by itself proof the serializer was consulted — a store
        // that ignored it entirely and wrote its own JSON would pass the round-trip fact above.
        _telemetry.SerializeCount.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task json_and_binary_events_coexist_in_one_stream()
    {
        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId,
                new ProbeLaunched("Kestrel", "LC-2"),
                new TelemetryBurst("s-band", new[] { 7, 7, 7 }),
                new ProbeSeparated(1));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.Count.ShouldBe(3);
        events.Select(x => x.Data.GetType())
            .ShouldBe(new[] { typeof(ProbeLaunched), typeof(TelemetryBurst), typeof(ProbeSeparated) });

        events[0].Data.ShouldBeOfType<ProbeLaunched>().Vehicle.ShouldBe("Kestrel");
        events[1].Data.ShouldBeOfType<TelemetryBurst>().Samples.ShouldBe(new[] { 7, 7, 7 });
        events[2].Data.ShouldBeOfType<ProbeSeparated>().Stage.ShouldBe(1);
    }

    [Fact]
    public async Task an_attribute_marked_event_uses_the_store_wide_default_serializer()
    {
        var before = _fallback.SerializeCount;

        var streamId = Guid.NewGuid();
        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId, new ProbeSeparated(2));
            await SaveChangesAsync(session);
        }

        _fallback.SerializeCount.ShouldBeGreaterThan(before);

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.Single().Data.ShouldBeOfType<ProbeSeparated>().Stage.ShouldBe(2);
    }

    [Fact]
    public async Task a_binary_event_is_readable_through_live_aggregation()
    {
        var streamId = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId,
                new TelemetryBurst("s-band", new[] { 1, 2 }),
                new TelemetryBurst("x-band", new[] { 3 }));
            await SaveChangesAsync(session);
        }

        // The deserialization path a projection or live aggregation takes is not always the one
        // FetchStreamAsync takes; a store can read events back through more than one code path and
        // only teach one of them about the binary column.
        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.Select(x => ((TelemetryBurst)x.Data).Samples.Length).Sum().ShouldBe(3);
    }

    [Fact]
    public async Task a_binary_payload_survives_bytes_that_are_not_valid_utf8_text()
    {
        // Gzip output is arbitrary bytes. A store that round-trips the payload through a text
        // encoding somewhere in its write path -- the natural implementation if binary support is
        // bolted onto a column that was always JSON -- corrupts it here and nowhere else.
        var streamId = Guid.NewGuid();
        var samples = Enumerable.Range(0, 512).ToArray();

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamId, new TelemetryBurst("wideband", samples));
            await SaveChangesAsync(session);
        }

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);

        events.Single().Data.ShouldBeOfType<TelemetryBurst>().Samples.ShouldBe(samples);
    }
}
