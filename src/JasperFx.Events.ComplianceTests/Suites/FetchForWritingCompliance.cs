using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Events.Projections;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Fetch-for-writing events and aggregate

public record AccountOpened(string Owner);

public record MoneyDeposited(decimal Amount);

public record MoneyWithdrawn(decimal Amount);

/// <summary>
/// A deliberately plain self-aggregating type. The point of this suite is the write handle and its
/// concurrency semantics, not the aggregation conventions, which
/// <see cref="SelfAggregatingEvolveCompliance{TFixture,TOperations,TQuerySession}"/> already covers.
/// </summary>
public partial class ComplianceAccount
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = string.Empty;
    public decimal Balance { get; set; }

    public static ComplianceAccount Create(AccountOpened e) => new() { Owner = e.Owner };

    public void Apply(MoneyDeposited e) => Balance += e.Amount;

    public void Apply(MoneyWithdrawn e) => Balance -= e.Amount;
}

#endregion

/// <summary>
/// The write handle contract — <c>FetchForWriting</c>, <c>FetchForExclusiveWriting</c>,
/// <c>WriteToAggregate</c>, <c>AppendOptimistic</c>/<c>AppendExclusive</c> and version-checked
/// <c>Append</c> — plus the concurrency failures all of them share.
/// </summary>
/// <remarks>
/// <para>
/// Every entry point here is declared on <see cref="IEventStoreOperations"/>, and the failure type is
/// shared too: <see cref="EventStreamUnexpectedMaxEventIdException"/> derives from JasperFx's
/// <see cref="ConcurrencyException"/> and both products throw it. So the suite needs no fixture
/// member of its own, and no exception abstraction.
/// </para>
/// <para>
/// The concurrency assertions deliberately wrap the whole fetch-append-save sequence and assert the
/// shared <see cref="ConcurrencyException"/> base rather than the exact derived type or the exact
/// call that throws. A store is free to detect the conflict eagerly at fetch time or late at commit
/// time — that is an implementation choice, not a behavioral contract, and pinning it would encode
/// one product's timing as the standard.
/// </para>
/// <para>
/// Guid stream identity only: stream identity is a store-level setting, so the string-keyed
/// equivalents live in
/// <see cref="StringStreamIdentityCompliance{TFixture,TOperations,TQuerySession}"/>.
/// </para>
/// </remarks>
public abstract class FetchForWritingCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_fetch_writing";
        config.Snapshot<ComplianceAccount>(SnapshotLifecycle.Inline);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private async Task<Guid> anOpenAccountAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var all = new List<object> { new AccountOpened("Hilda") };
        all.AddRange(events);
        EventsFor(session).StartStream<ComplianceAccount>(streamId, all);
        await SaveChangesAsync(session);

        return streamId;
    }

    [Fact]
    public async Task fetch_for_writing_a_stream_that_does_not_exist_yet()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceAccount>(streamId, Cancellation);

        stream.Aggregate.ShouldBeNull();
        stream.Id.ShouldBe(streamId);

        // Zero, not null. Both products report a not-yet-existing stream as version 0, and the
        // XML docs on IEventStream<T> used to claim null -- this suite is what caught that.
        stream.StartingVersion.ShouldBe(0);
        stream.CurrentVersion.ShouldBe(0);

        stream.AppendOne(new AccountOpened("Hilda"));
        await SaveChangesAsync(session);

        var account = await LoadDocumentAsync<ComplianceAccount>(session, streamId);
        account.ShouldNotBeNull();
        account.Owner.ShouldBe("Hilda");
    }

    [Fact]
    public async Task fetch_for_writing_an_existing_stream_returns_current_state_and_version()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100), new MoneyDeposited(50));

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceAccount>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(150);
        stream.StartingVersion.ShouldBe(3);
        stream.CurrentVersion.ShouldBe(3);
    }

    [Fact]
    public async Task appending_to_the_handle_advances_current_version_but_not_starting_version()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceAccount>(streamId, Cancellation);

        stream.AppendOne(new MoneyDeposited(25));
        stream.AppendMany(new MoneyWithdrawn(5), new MoneyWithdrawn(10));

        stream.StartingVersion.ShouldBe(2);
        stream.CurrentVersion.ShouldBe(5);
        stream.Events.Count.ShouldBe(3);

        await SaveChangesAsync(session);

        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(5);
    }

    [Fact]
    public async Task fetch_for_writing_with_a_matching_expected_version_commits()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        var stream = await EventsFor(session).FetchForWriting<ComplianceAccount>(streamId, 2, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(100);

        stream.AppendOne(new MoneyDeposited(1));
        await SaveChangesAsync(session);

        var account = await LoadDocumentAsync<ComplianceAccount>(session, streamId);
        account.ShouldNotBeNull();
        account.Balance.ShouldBe(101);
    }

    [Fact]
    public async Task fetch_for_writing_with_a_stale_expected_version_fails()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100), new MoneyDeposited(50));

        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();

            // The stream is at version 3, so 2 is a stale read. Whether the store rejects this at
            // fetch time or at commit time is deliberately not pinned.
            var stream = await EventsFor(session).FetchForWriting<ComplianceAccount>(streamId, 2, Cancellation);
            stream.AppendOne(new MoneyDeposited(1));
            await SaveChangesAsync(session);
        });
    }

    [Fact]
    public async Task appending_with_a_stale_expected_version_fails()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();
            EventsFor(session).Append(streamId, 99, new MoneyDeposited(1));
            await SaveChangesAsync(session);
        });
    }

    [Fact]
    public async Task fetch_many_for_writing_returns_one_handle_per_id_in_order()
    {
        var rich = await anOpenAccountAsync(new MoneyDeposited(100), new MoneyDeposited(50));
        var missing = Guid.NewGuid();
        var poor = await anOpenAccountAsync(new MoneyWithdrawn(5));

        await using var session = OpenSession();
        var streams = await EventsFor(session)
            .FetchManyForWriting<ComplianceAccount>(new[] { rich, missing, poor }, Cancellation);

        // In the order asked for, so a caller can zip the result against its ids (jasperfx#930).
        streams.Select(x => x.Id).ShouldBe(new[] { rich, missing, poor });

        streams[0].Aggregate.ShouldNotBeNull();
        streams[0].Aggregate!.Balance.ShouldBe(150);
        streams[0].StartingVersion.ShouldBe(3);

        // A stream that does not exist yet is a handle at version 0, exactly as FetchForWriting has it.
        streams[1].Aggregate.ShouldBeNull();
        streams[1].StartingVersion.ShouldBe(0);

        streams[2].Aggregate.ShouldNotBeNull();
        streams[2].Aggregate!.Balance.ShouldBe(-5);
        streams[2].StartingVersion.ShouldBe(2);
    }

    [Fact]
    public async Task fetch_many_for_writing_commits_appends_to_every_handle()
    {
        var first = await anOpenAccountAsync(new MoneyDeposited(10));
        var brandNew = Guid.NewGuid();

        await using (var session = OpenSession())
        {
            var streams = await EventsFor(session)
                .FetchManyForWriting<ComplianceAccount>(new[] { first, brandNew }, Cancellation);

            streams[0].AppendOne(new MoneyDeposited(1));
            streams[1].AppendOne(new AccountOpened("Gunnar"));
            await SaveChangesAsync(session);
        }

        await using var reader = OpenSession();
        (await LoadDocumentAsync<ComplianceAccount>(reader, first))!.Balance.ShouldBe(11);
        (await LoadDocumentAsync<ComplianceAccount>(reader, brandNew))!.Owner.ShouldBe("Gunnar");
    }

    /// <remarks>
    /// The point of fetching many for writing rather than reading many: every handle keeps its own
    /// starting version, so a concurrent write to any one of the streams still fails the commit.
    /// </remarks>
    [Fact]
    public async Task fetch_many_for_writing_guards_each_stream_on_its_own_version()
    {
        var untouched = await anOpenAccountAsync(new MoneyDeposited(100));
        var contended = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        var streams = await EventsFor(session)
            .FetchManyForWriting<ComplianceAccount>(new[] { untouched, contended }, Cancellation);

        await using (var rival = OpenSession())
        {
            var stream = await EventsFor(rival).FetchForWriting<ComplianceAccount>(contended, Cancellation);
            stream.AppendOne(new MoneyDeposited(10));
            await SaveChangesAsync(rival);
        }

        streams[0].AppendOne(new MoneyDeposited(1));
        streams[1].AppendOne(new MoneyDeposited(20));
        await ShouldFailWithAsync<ConcurrencyException>(() => SaveChangesAsync(session));

        await using var reader = OpenSession();
        (await LoadDocumentAsync<ComplianceAccount>(reader, contended))!.Balance.ShouldBe(110);
    }

    [Fact]
    public async Task fetch_many_for_writing_rejects_a_repeated_id()
    {
        var streamId = await anOpenAccountAsync();

        await using var session = OpenSession();
        await Should.ThrowAsync<ArgumentException>(() => EventsFor(session)
            .FetchManyForWriting<ComplianceAccount>(new[] { streamId, streamId }, Cancellation));
    }

    [Fact]
    public async Task fetch_many_for_writing_with_no_ids_returns_no_handles()
    {
        await using var session = OpenSession();
        var streams = await EventsFor(session)
            .FetchManyForWriting<ComplianceAccount>(Array.Empty<Guid>(), Cancellation);

        streams.ShouldBeEmpty();
    }

    [Fact]
    public async Task two_writers_racing_on_the_same_starting_version_lose_the_second_write()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var first = OpenSession();
        await using var second = OpenSession();

        var firstStream = await EventsFor(first).FetchForWriting<ComplianceAccount>(streamId, 2, Cancellation);
        var secondStream = await EventsFor(second).FetchForWriting<ComplianceAccount>(streamId, 2, Cancellation);

        firstStream.AppendOne(new MoneyDeposited(10));
        await SaveChangesAsync(first);

        secondStream.AppendOne(new MoneyDeposited(20));
        await ShouldFailWithAsync<ConcurrencyException>(() => SaveChangesAsync(second));

        // The winner's write stands, the loser's does not.
        await using var reader = OpenSession();
        var account = await LoadDocumentAsync<ComplianceAccount>(reader, streamId);
        account.ShouldNotBeNull();
        account.Balance.ShouldBe(110);
    }

    [Fact]
    public async Task fetch_for_exclusive_writing_returns_the_same_state_and_commits()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        var stream = await EventsFor(session)
            .FetchForExclusiveWriting<ComplianceAccount>(streamId, Cancellation);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate.Balance.ShouldBe(100);

        stream.AppendOne(new MoneyDeposited(5));
        await SaveChangesAsync(session);

        var account = await LoadDocumentAsync<ComplianceAccount>(session, streamId);
        account.ShouldNotBeNull();
        account.Balance.ShouldBe(105);
    }

    [Fact]
    public async Task write_to_aggregate_with_the_action_overload()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        await EventsFor(session).WriteToAggregate<ComplianceAccount>(streamId,
            stream =>
            {
                stream.Aggregate.ShouldNotBeNull();
                stream.Aggregate.Balance.ShouldBe(100);
                stream.AppendOne(new MoneyWithdrawn(40));
            }, Cancellation);

        await using var reader = OpenSession();
        var account = await LoadDocumentAsync<ComplianceAccount>(reader, streamId);
        account.ShouldNotBeNull();
        account.Balance.ShouldBe(60);
    }

    [Fact]
    public async Task write_to_aggregate_with_the_async_overload()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        await EventsFor(session).WriteToAggregate<ComplianceAccount>(streamId,
            async stream =>
            {
                await Task.Yield();
                stream.AppendOne(new MoneyDeposited(11));
            }, Cancellation);

        await using var reader = OpenSession();
        var account = await LoadDocumentAsync<ComplianceAccount>(reader, streamId);
        account.ShouldNotBeNull();
        account.Balance.ShouldBe(111);
    }

    [Fact]
    public async Task write_to_aggregate_with_a_stale_expected_version_fails()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await ShouldFailWithAsync<ConcurrencyException>(async () =>
        {
            await using var session = OpenSession();
            await EventsFor(session).WriteToAggregate<ComplianceAccount>(streamId, 99,
                stream => stream.AppendOne(new MoneyDeposited(1)), Cancellation);
        });
    }

    [Fact]
    public async Task append_optimistic_against_an_existing_stream()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        await EventsFor(session).AppendOptimistic(streamId, Cancellation, new MoneyDeposited(7));
        await SaveChangesAsync(session);

        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
    }

    [Fact]
    public async Task append_exclusive_against_an_existing_stream()
    {
        var streamId = await anOpenAccountAsync(new MoneyDeposited(100));

        await using var session = OpenSession();
        await EventsFor(session).AppendExclusive(streamId, Cancellation, new MoneyDeposited(3));
        await SaveChangesAsync(session);

        var state = await EventsFor(session).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
    }
}
