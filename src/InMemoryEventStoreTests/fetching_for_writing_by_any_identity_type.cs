using JasperFx;
using JasperFx.Events;
using JasperFx.Events.InMemory;
using Shouldly;

namespace InMemoryEventStoreTests;

public record LedgerId(Guid Value);

public record LedgerPosted(int Amount);

public partial class Ledger
{
    public Guid Id { get; set; }

    public int Balance { get; set; }

    public void Apply(LedgerPosted e) => Balance += e.Amount;
}

/// <summary>
/// The in-memory store's <c>FetchForWriting&lt;T, TId&gt;(id, expectedVersion)</c> and
/// <c>FetchManyForWriting&lt;T, TId&gt;(ids)</c>, by a stream id or a strong-typed identifier wrapping one.
/// </summary>
public class fetching_for_writing_by_any_identity_type
{
    private readonly InMemoryDocumentStore theStore = new();

    private async Task<Guid> startLedger(params int[] amounts)
    {
        var id = Guid.NewGuid();
        await using var session = theStore.LightweightSession();
        session.Events.StartStream<Ledger>(id, amounts.Select(x => (object)new LedgerPosted(x)).ToArray());
        await session.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task fetch_for_writing_by_a_strong_typed_id_at_its_current_version()
    {
        var id = await startLedger(10, 5);

        await using var session = theStore.LightweightSession();
        var stream = await session.Events.FetchForWriting<Ledger, LedgerId>(new LedgerId(id), 2);

        stream.Id.ShouldBe(id);
        stream.CurrentVersion.ShouldBe(2);
        stream.Aggregate!.Balance.ShouldBe(15);
    }

    [Fact]
    public async Task fetch_for_writing_by_a_strong_typed_id_at_a_stale_version_fails_at_once()
    {
        var id = await startLedger(10, 5);

        await using var session = theStore.LightweightSession();

        await Should.ThrowAsync<ConcurrencyException>(
            () => session.Events.FetchForWriting<Ledger, LedgerId>(new LedgerId(id), 1));
    }

    [Fact]
    public async Task fetch_for_writing_by_the_stream_id_through_the_generic_form()
    {
        var id = await startLedger(10);

        await using var session = theStore.LightweightSession();
        var stream = await session.Events.FetchForWriting<Ledger, Guid>(id, 1);

        stream.Aggregate!.Balance.ShouldBe(10);
    }

    [Fact]
    public async Task fetch_many_for_writing_returns_one_handle_per_id_in_order()
    {
        var first = await startLedger(1);
        var second = await startLedger(2, 3);
        var unknown = Guid.NewGuid();

        await using var session = theStore.LightweightSession();
        var streams = await session.Events.FetchManyForWriting<Ledger, LedgerId>(
            [new LedgerId(second), new LedgerId(unknown), new LedgerId(first)]);

        streams.Select(x => x.Id).ShouldBe([second, unknown, first]);
        streams.Select(x => x.CurrentVersion).ShouldBe([2L, 0L, 1L]);
        streams[0].Aggregate!.Balance.ShouldBe(5);
        streams[1].Aggregate.ShouldBeNull();
        streams[2].Aggregate!.Balance.ShouldBe(1);
    }

    [Fact]
    public async Task fetch_many_for_writing_refuses_a_nullable_id_type()
    {
        await using var session = theStore.LightweightSession();

        // The nullable form is the one the method refuses
#pragma warning disable CS8714
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchManyForWriting<Ledger, Guid?>([Guid.NewGuid()]));
#pragma warning restore CS8714

        ex.Message.ShouldContain("not Guid?");
    }

    [Fact]
    public async Task fetch_many_for_writing_refuses_a_null_id_and_names_its_position()
    {
        await using var session = theStore.LightweightSession();

        var ex = await Should.ThrowAsync<ArgumentNullException>(
            () => session.Events.FetchManyForWriting<Ledger, LedgerId>([new LedgerId(Guid.NewGuid()), null!]));

        ex.Message.ShouldContain("position 1");
    }

    [Fact]
    public async Task fetch_many_for_writing_refuses_a_repeated_id()
    {
        var id = Guid.NewGuid();
        await using var session = theStore.LightweightSession();

        await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchManyForWriting<Ledger, LedgerId>([new LedgerId(id), new LedgerId(id)]));
    }

    [Fact]
    public async Task an_id_type_that_does_not_wrap_the_stream_identity_is_not_supported()
    {
        await using var session = theStore.LightweightSession();

        await Should.ThrowAsync<NotSupportedException>(
            () => session.Events.FetchForWriting<Ledger, string>("not-a-stream-id", 1));
    }
}
