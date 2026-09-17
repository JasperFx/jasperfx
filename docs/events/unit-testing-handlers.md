# Unit Testing a Handler that Takes an Event Stream

Wolverine's aggregate handler workflow can hand a command handler the stream handle itself:

```csharp
public static void Handle(Withdraw command, [WriteAggregate] IEventStream<Account> stream)
```

`IEventStream<T>` lives in `JasperFx.Events`, so Marten, Polecat and Fisher all satisfy the same contract. That makes the handler a pure function of *the aggregate state it was given* and *the events it appends* — which is exactly the shape you can test without a database.

`StubEventStream<T>` is the stand-in. Construct one with the aggregate state the handler should see, call the handler, and assert on what it appended.

## The subject

<!-- snippet: sample_stub_event_stream_subject -->
<a id='snippet-sample_stub_event_stream_subject'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/Events/StubEventStreamSamples.cs#L5-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_subject' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Arrange, act, assert

<!-- snippet: sample_stub_event_stream_basic_usage -->
<a id='snippet-sample_stub_event_stream_basic_usage'></a>
```cs
// Arrange -- the aggregate state the handler should see. No database, no mocks.
var stream = new StubEventStream<Account>(new Account(Guid.NewGuid(), 500m, false));

// Act
WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

// Assert on what the handler actually produced
var withdrawn = (FundsWithdrawn)stream.EventsAppended.Single();
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/Events/StubEventStreamSamples.cs#L45-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_basic_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That is the whole pattern. `EventsAppended` is every event the handler emitted, in order, whichever of `AppendOne` / `AppendMany` it used.

## Don't reach for a mocking library here

::: tip
This is the recommended alternative to mocking `IEventStream<T>` with NSubstitute or Moq.
:::

A mock-based version of the test above asserts something weaker than it looks:

```csharp
// Proves that a method was called. Says nothing about what the handler decided.
stream.Received(1).AppendOne(Arg.Any<FundsWithdrawn>());
```

Verifying the *call* leaves the interesting part unchecked — was it `FundsWithdrawn` for 100, or for 0, or `WithdrawalRejected`? A recorded list of events is the thing the handler is actually supposed to produce, so assert on that instead. The stub is also shorter to set up than the mock it replaces, and it has no per-store variant to get wrong.

## A stream that does not exist yet

`Aggregate` is null for a stream a real fetch did not find, which is how a handler decides between starting a stream and appending to one. Pass null:

<!-- snippet: sample_stub_event_stream_missing_stream -->
<a id='snippet-sample_stub_event_stream_missing_stream'></a>
```cs
// A null aggregate is what a handler sees for a stream that does not exist yet
var stream = new StubEventStream<Account>(null);

WithdrawHandler.Handle(new Withdraw(Guid.NewGuid(), 100m), stream);

var rejected = (WithdrawalRejected)stream.EventsAppended.Single();
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/Events/StubEventStreamSamples.cs#L63-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_missing_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Versions and identity

`Id` and `Key` are both populated by default, because the stub cannot know which identity style the handler under test reads. Set whichever it does — wiring a command's id to the stream is what makes a multi-stream test read straight. `StartingVersion` and `CurrentVersion` are unset by default and settable, for a handler that branches on the version it was handed:

<!-- snippet: sample_stub_event_stream_versions_and_identity -->
<a id='snippet-sample_stub_event_stream_versions_and_identity'></a>
```cs
var stream = new StubEventStream<Account>(new Account(Guid.NewGuid(), 500m, false))
{
    // Wire a command's id to the stream in a multi-stream test...
    Id = Guid.NewGuid(),

    // ...and exercise a version-sensitive handler
    StartingVersion = 3,
    CurrentVersion = 3,
};
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/Events/StubEventStreamSamples.cs#L79-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_versions_and_identity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AlwaysEnforceConsistency` round-trips, and `TryFastForwardVersion()` does nothing — there is no optimistic concurrency check in a stub to fast-forward.

## The `IEvent` envelopes

`EventsAppended` is what most assertions want. `Events` is the interface's own member, so it is there for a handler or an assertion that reads it:

<!-- snippet: sample_stub_event_stream_envelopes -->
<a id='snippet-sample_stub_event_stream_envelopes'></a>
```cs
var stream = new StubEventStream<Account>(new Account(Guid.NewGuid(), 500m, false));
stream.AppendOne(new FundsWithdrawn(100m));

// IEventStream<T>.Events, wrapped as the interface declares it. Carries the event type
// naming and nothing a real store would only know at save time -- no sequence, no version.
var envelope = stream.Events.Single();
var typeName = envelope.EventTypeName; // "funds_withdrawn"
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/Events/StubEventStreamSamples.cs#L96-L106' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stub_event_stream_envelopes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The envelopes are built by an `EventRegistry`, which has no store behind it. They carry the conventional event type naming and nothing a real store would only know at save time — no sequence, no version, no assigned timestamp. If your application customizes event type aliases and the assertions care, pass a configured `IEventRegistry` to the second constructor overload.

::: warning
`StubEventStream<T>` records; it does not persist, project or validate. It will happily accept events a real store would reject, and it never raises a concurrency exception. Use it to test the *decision* a handler makes; use an integration test against a real store to test that the decision is stored correctly.
:::

## Marten's `StubEventStream<T>`

The Marten-specific `Marten.Events.StubEventStream<T>` predates this one and still works. It was built on Marten's own `StoreOptions` / `EventGraph`, which is what left Polecat and Fisher users to write their own or reach for a mock. New tests should prefer `JasperFx.Events.StubEventStream<T>`, which reads the same and works against any of the three stores.
