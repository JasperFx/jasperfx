using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace EventTests.Daemon;

/// <summary>
/// jasperfx#1150: FetchFirstEventTimestampsAfterAsync ships as a default interface member so the out-of-tree
/// IEventDatabase implementors keep compiling. A null timestamp means "caught up", so the default must throw
/// rather than answer, and the single-position convenience must go through the batched member.
/// </summary>
public class PendingEventAgeDefaultsTests
{
    private class BareEventDatabase : IEventDatabase
    {
        public string Identifier => "bare";
        public Uri DatabaseUri => new("test://bare");
        public ShardStateTracker Tracker => throw new NotImplementedException();
        public string StorageIdentifier => "bare";

        public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
            => throw new NotImplementedException();

        public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
            => throw new NotImplementedException();

        public Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout) => throw new NotImplementedException();

        public Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
            => throw new NotImplementedException();

        public Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
            => throw new NotImplementedException();

        public Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
            => throw new NotImplementedException();
    }

    private sealed class RecordingEventDatabase : BareEventDatabase, IEventDatabase
    {
        public static readonly DateTimeOffset Stamp = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        public List<(string? TenantId, long Sequence)> Asked { get; } = new();

        public Task<IReadOnlyList<PendingEventAge>> FetchFirstEventTimestampsAfterAsync(
            IReadOnlyList<(string? TenantId, long Sequence)> positions, CancellationToken token = default)
        {
            Asked.AddRange(positions);
            var answers = new List<PendingEventAge>();
            foreach (var (tenantId, sequence) in positions)
            {
                answers.Add(new PendingEventAge(tenantId, sequence, sequence < 10 ? Stamp : null));
            }

            return Task.FromResult<IReadOnlyList<PendingEventAge>>(answers);
        }
    }

    [Fact]
    public async Task the_default_throws_rather_than_reporting_caught_up()
    {
        IEventDatabase database = new BareEventDatabase();

        var ex = await Should.ThrowAsync<NotSupportedException>(() =>
            database.FetchFirstEventTimestampsAfterAsync([(null, 5)], CancellationToken.None));

        ex.Message.ShouldContain("jasperfx#1150");
        ex.Message.ShouldContain(nameof(BareEventDatabase));
    }

    [Fact]
    public async Task the_single_position_default_also_throws_on_a_store_that_has_not_implemented_it()
    {
        IEventDatabase database = new BareEventDatabase();

        await Should.ThrowAsync<NotSupportedException>(() =>
            database.FetchFirstEventTimestampAfterAsync(5, null, CancellationToken.None));
    }

    [Fact]
    public async Task the_single_position_default_goes_through_the_batched_member()
    {
        var recording = new RecordingEventDatabase();
        IEventDatabase database = recording;

        (await database.FetchFirstEventTimestampAfterAsync(3, "acme", CancellationToken.None))
            .ShouldBe(RecordingEventDatabase.Stamp);
        (await database.FetchFirstEventTimestampAfterAsync(12, null, CancellationToken.None))
            .ShouldBeNull();

        recording.Asked.ShouldBe([("acme", 3L), (null, 12L)]);
    }

    [Fact]
    public void a_pending_age_with_no_timestamp_is_caught_up_and_has_no_age()
    {
        var age = new PendingEventAge(null, 100, null);

        age.IsCaughtUp.ShouldBeTrue();
        age.AgeAt(DateTimeOffset.UtcNow).ShouldBeNull();
    }

    [Fact]
    public void the_age_is_measured_from_the_oldest_pending_event()
    {
        var stamp = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var age = new PendingEventAge("acme", 100, stamp);

        age.IsCaughtUp.ShouldBeFalse();
        age.AgeAt(stamp.AddSeconds(40)).ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void clock_skew_never_reports_a_negative_age()
    {
        var stamp = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        new PendingEventAge(null, 1, stamp).AgeAt(stamp.AddSeconds(-3)).ShouldBe(TimeSpan.Zero);
    }
}
