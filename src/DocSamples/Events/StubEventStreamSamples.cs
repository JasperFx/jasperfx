using JasperFx.Events;

namespace DocSamples.Events;

#region sample_stub_event_stream_subject

public record Account(Guid Id, decimal Balance, bool IsFrozen);

public record Withdraw(Guid AccountId, decimal Amount);

public record FundsWithdrawn(decimal Amount);

public record WithdrawalRejected(string Reason);

public static class WithdrawHandler
{
    // Wolverine's aggregate handler workflow hands the handler a live stream handle:
    //     public static void Handle(Withdraw command, [WriteAggregate] IEventStream<Account> stream)
    public static void Handle(Withdraw command, IEventStream<Account> stream)
    {
        var account = stream.Aggregate;

        if (account is null || account.IsFrozen)
        {
            stream.AppendOne(new WithdrawalRejected("Account is not available"));
            return;
        }

        if (account.Balance < command.Amount)
        {
            stream.AppendOne(new WithdrawalRejected("Insufficient funds"));
            return;
        }

        stream.AppendOne(new FundsWithdrawn(command.Amount));
    }
}

#endregion

public class StubEventStreamSamples
{
    public FundsWithdrawn the_happy_path()
    {
        #region sample_stub_event_stream_basic_usage

        // Arrange -- the aggregate state the handler should see. No database, no mocks.
        var stream = new StubEventStream<Account>(new Account(Guid.NewGuid(), 500m, false));

        // Act
        WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

        // Assert on what the handler actually produced
        var withdrawn = (FundsWithdrawn)stream.EventsAppended.Single();

        #endregion

        return withdrawn;
    }

    public WithdrawalRejected the_stream_does_not_exist_yet()
    {
        #region sample_stub_event_stream_missing_stream

        // A null aggregate is what a handler sees for a stream that does not exist yet
        var stream = new StubEventStream<Account>(null);

        WithdrawHandler.Handle(new Withdraw(Guid.NewGuid(), 100m), stream);

        var rejected = (WithdrawalRejected)stream.EventsAppended.Single();

        #endregion

        return rejected;
    }

    public void version_and_identity()
    {
        #region sample_stub_event_stream_versions_and_identity

        var stream = new StubEventStream<Account>(new Account(Guid.NewGuid(), 500m, false))
        {
            // Wire a command's id to the stream in a multi-stream test...
            Id = Guid.NewGuid(),

            // ...and exercise a version-sensitive handler
            StartingVersion = 3,
            CurrentVersion = 3,
        };

        #endregion
    }

    public void envelopes()
    {
        #region sample_stub_event_stream_envelopes

        var stream = new StubEventStream<Account>(new Account(Guid.NewGuid(), 500m, false));
        stream.AppendOne(new FundsWithdrawn(100m));

        // IEventStream<T>.Events, wrapped as the interface declares it. Carries the event type
        // naming and nothing a real store would only know at save time -- no sequence, no version.
        var envelope = stream.Events.Single();
        var typeName = envelope.EventTypeName; // "funds_withdrawn"

        #endregion
    }
}
