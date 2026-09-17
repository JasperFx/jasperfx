using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// jasperfx#858: a store-agnostic stand-in for <see cref="IEventStream{T}"/>, so a handler that
/// takes a stream handle can be unit tested without a database and without a mocking library.
/// </summary>
public class StubEventStreamTests
{
    private static StubEventStream<Account> Stream(Account? account = null)
        => new(account ?? new Account(500m, false));

    #region recording appends

    [Fact]
    public void append_one_is_recorded()
    {
        var stream = Stream();
        stream.AppendOne(new FundsWithdrawn(100m));

        stream.EventsAppended.ShouldHaveSingleItem().ShouldBeOfType<FundsWithdrawn>().Amount.ShouldBe(100m);
    }

    [Fact]
    public void append_many_by_params_is_recorded_in_order()
    {
        var stream = Stream();
        stream.AppendMany(new FundsWithdrawn(100m), new WithdrawalRejected("Frozen"));

        stream.EventsAppended.Select(x => x.GetType()).ShouldBe([typeof(FundsWithdrawn), typeof(WithdrawalRejected)]);
    }

    [Fact]
    public void append_many_by_enumerable_is_recorded_in_order()
    {
        var stream = Stream();
        stream.AppendMany(new List<object> { new FundsWithdrawn(100m), new FundsWithdrawn(50m) });

        stream.EventsAppended.Cast<FundsWithdrawn>().Select(x => x.Amount).ShouldBe([100m, 50m]);
    }

    [Fact]
    public void every_append_path_lands_in_one_list_in_call_order()
    {
        var stream = Stream();

        stream.AppendOne(new FundsWithdrawn(1m));
        stream.AppendMany(new FundsWithdrawn(2m));
        stream.AppendMany(new List<object> { new FundsWithdrawn(3m) });

        stream.EventsAppended.Cast<FundsWithdrawn>().Select(x => x.Amount).ShouldBe([1m, 2m, 3m]);
    }

    [Fact]
    public void a_fresh_stub_has_appended_nothing()
    {
        Stream().EventsAppended.ShouldBeEmpty();
    }

    #endregion

    #region the interface's own members

    [Fact]
    public void the_aggregate_is_what_the_handler_was_given()
    {
        var account = new Account(500m, false);
        new StubEventStream<Account>(account).Aggregate.ShouldBeSameAs(account);
    }

    /// <summary>
    /// A null aggregate is what a real fetch hands a handler for a stream that does not exist yet,
    /// so the stub has to be able to say it.
    /// </summary>
    [Fact]
    public void the_aggregate_can_be_null()
    {
        new StubEventStream<Account>(null).Aggregate.ShouldBeNull();
    }

    [Fact]
    public void the_versions_are_settable_for_a_version_sensitive_handler()
    {
        var stream = Stream();

        // Unset by default -- the stub does not guess a version for you
        stream.StartingVersion.ShouldBeNull();
        stream.CurrentVersion.ShouldBeNull();

        stream.StartingVersion = 3;
        stream.CurrentVersion = 5;

        stream.StartingVersion.ShouldBe(3);
        stream.CurrentVersion.ShouldBe(5);
    }

    /// <summary>
    /// Both identities are populated, because the stub cannot know which style the handler under
    /// test reads. Either can be set to wire a command's id to the stream.
    /// </summary>
    [Fact]
    public void the_identities_are_populated_and_settable()
    {
        var stream = Stream();

        stream.Id.ShouldNotBe(Guid.Empty);
        stream.Key.ShouldNotBeNullOrEmpty();

        var id = Guid.NewGuid();
        stream.Id = id;
        stream.Key = "account/1";

        stream.Id.ShouldBe(id);
        stream.Key.ShouldBe("account/1");
    }

    [Fact]
    public void two_stubs_do_not_share_an_identity()
    {
        Stream().Id.ShouldNotBe(Stream().Id);
    }

    [Fact]
    public void the_cancellation_token_is_settable()
    {
        using var source = new CancellationTokenSource();

        Stream().Cancellation.ShouldBe(CancellationToken.None);
        new StubEventStream<Account>(null) { Cancellation = source.Token }.Cancellation.ShouldBe(source.Token);
    }

    [Fact]
    public void always_enforce_consistency_round_trips()
    {
        var stream = Stream();

        stream.AlwaysEnforceConsistency.ShouldBeFalse();
        stream.AlwaysEnforceConsistency = true;
        stream.AlwaysEnforceConsistency.ShouldBeTrue();
    }

    [Fact]
    public void try_fast_forward_version_is_inert()
    {
        var stream = Stream();
        stream.StartingVersion = 2;
        stream.AppendOne(new FundsWithdrawn(100m));

        stream.TryFastForwardVersion();

        // Nothing to fast forward in a stub, and nothing lost either
        stream.StartingVersion.ShouldBe(2);
        stream.EventsAppended.ShouldHaveSingleItem();
    }

    #endregion

    #region IEvent envelopes, without a store

    /// <summary>
    /// The whole point of lifting the stub out of Marten: the envelopes are built from
    /// <see cref="EventRegistry"/>, which has no store behind it.
    /// </summary>
    [Fact]
    public void events_are_wrapped_in_envelopes_carrying_the_conventional_type_name()
    {
        var stream = Stream();
        stream.AppendOne(new FundsWithdrawn(100m));

        var envelope = stream.Events.ShouldHaveSingleItem();

        envelope.ShouldBeOfType<Event<FundsWithdrawn>>();
        envelope.EventType.ShouldBe(typeof(FundsWithdrawn));
        envelope.EventTypeName.ShouldBe("funds_withdrawn");
        envelope.Data.ShouldBeOfType<FundsWithdrawn>().Amount.ShouldBe(100m);
    }

    [Fact]
    public void the_envelopes_follow_the_appended_events_in_order()
    {
        var stream = Stream();
        stream.AppendMany(new FundsWithdrawn(100m), new WithdrawalRejected("Frozen"));

        stream.Events.Select(x => x.EventType).ShouldBe([typeof(FundsWithdrawn), typeof(WithdrawalRejected)]);
        stream.Events.Count.ShouldBe(stream.EventsAppended.Count);
    }

    /// <summary>
    /// The overload exists for a test whose assertions care about event type naming the application
    /// has customized.
    /// </summary>
    [Fact]
    public void a_supplied_registry_decides_the_type_names()
    {
        var registry = new EventRegistry();
        registry.EventMappingFor(typeof(FundsWithdrawn)).EventTypeName = "withdrew";

        var stream = new StubEventStream<Account>(new Account(500m, false), registry);
        stream.AppendOne(new FundsWithdrawn(100m));

        stream.Events.ShouldHaveSingleItem().EventTypeName.ShouldBe("withdrew");
    }

    [Fact]
    public void a_null_registry_is_rejected_where_it_is_given_rather_than_where_it_is_used()
    {
        Should.Throw<ArgumentNullException>(() => new StubEventStream<Account>(null, null!));
    }

    #endregion

    #region the scenario this exists for

    /// <summary>
    /// The handler shape the issue is about: Wolverine hands a handler an
    /// <c>IEventStream&lt;T&gt;</c> through <c>[WriteAggregate]</c>, and the recommended test is to
    /// pass a stand-in, call <c>Handle</c>, and assert on the events appended — no database, and no
    /// <c>Received(1).AppendOne(…)</c>, which proves only that a method was called.
    /// </summary>
    [Theory]
    [InlineData(500, false, 100, typeof(FundsWithdrawn))]
    [InlineData(50, false, 100, typeof(WithdrawalRejected))]
    [InlineData(500, true, 100, typeof(WithdrawalRejected))]
    public void unit_testing_a_handler_that_takes_a_stream(decimal balance, bool frozen, decimal amount,
        Type expected)
    {
        var stream = new StubEventStream<Account>(new Account(balance, frozen));

        Handle(amount, stream);

        stream.EventsAppended.ShouldHaveSingleItem().ShouldBeOfType(expected);
    }

    [Fact]
    public void a_handler_can_see_the_stream_does_not_exist_yet()
    {
        var stream = new StubEventStream<Account>(null);

        Handle(100m, stream);

        stream.EventsAppended.ShouldHaveSingleItem().ShouldBeOfType<WithdrawalRejected>();
    }

    private static void Handle(decimal amount, IEventStream<Account> stream)
    {
        var account = stream.Aggregate;

        if (account is null || account.IsFrozen)
        {
            stream.AppendOne(new WithdrawalRejected("Account is not available"));
        }
        else if (account.Balance < amount)
        {
            stream.AppendOne(new WithdrawalRejected("Insufficient funds"));
        }
        else
        {
            stream.AppendOne(new FundsWithdrawn(amount));
        }
    }

    #endregion

    public record Account(decimal Balance, bool IsFrozen);

    public record FundsWithdrawn(decimal Amount);

    public record WithdrawalRejected(string Reason);
}
