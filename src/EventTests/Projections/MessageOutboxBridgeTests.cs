using JasperFx.Events;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Daemon;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;

namespace EventTests.Projections
{
    // jasperfx#1045. Consolidating IMessageOutbox and IMessageBatch breaks Wolverine.Polecat and Wolverine.Fisher,
    // so a store's interfaces derive from the shared ones for one release. BridgedStore below is that release:
    // the declarations are the ones the stores will write, and WolverineShapedOutbox is what Wolverine's bridge
    // classes look like today, unedited. If this file compiles, the bridge works.
    public class MessageOutboxBridgeTests
    {
        [Fact]
        public async Task an_implementer_written_against_a_store_interface_works_through_the_shared_one()
        {
            var implementer = new WolverineShapedOutbox();

            // The assignment the store's options make: typed to the shared interface, handed the store's own
            IMessageOutbox shared = implementer;

            var session = Substitute.For<BridgedStore.IStoreSession>();

            var batch = await shared.CreateBatch(session);

            implementer.Session.ShouldBeSameAs(session);
            batch.ShouldBeOfType<WolverineShapedBatch>();
        }

        [Fact]
        public async Task a_store_calling_its_own_overload_still_gets_its_own_batch_type()
        {
            BridgedStore.IMessageOutbox outbox = new WolverineShapedOutbox();

            // Binds to the store's overload, because a store session is the better conversion
            BridgedStore.IMessageBatch batch = await outbox.CreateBatch(Substitute.For<BridgedStore.IStoreSession>());

            batch.ShouldNotBeNull();
        }

        [Fact]
        public async Task a_store_implementer_runs_through_the_shared_batch_base()
        {
            var implementer = new WolverineShapedOutbox();
            var batch = new BridgedBatch(implementer);
            batch.SessionForTenant("tenant-a");

            await batch.PublishMessageAsync(new MessageOutboxBridgeTests.Ping(), "tenant-a");
            await batch.ExecuteAsync(CancellationToken.None);

            implementer.Batch!.Published.ShouldBe([typeof(MessageOutboxBridgeTests.Ping)]);
            implementer.Batch.Hooks.ShouldBe(["before", "after"]);
        }

        // ComplianceTests ships as source. EventTests compiles RecordingMessageOutbox.cs the way a consumer does,
        // beside a consumer's half (below) that has not changed, so that this is the consumer's build.
        [Fact]
        public async Task the_shipped_recording_outbox_works_through_the_shared_interfaces()
        {
            var outbox = new RecordingMessageOutbox();
            var batch = new BridgedBatch(outbox);
            batch.SessionForTenant("tenant-a");

            await batch.PublishMessageAsync(new MessageOutboxBridgeTests.Ping(), "tenant-a");
            await batch.ExecuteAsync(CancellationToken.None);

            outbox.PublishedWithTenant.ShouldHaveSingleItem().TenantId.ShouldBe("tenant-a");
            outbox.Batches.ShouldHaveSingleItem().Hooks.ShouldBe(["before", "after"]);
        }

        [Fact]
        public async Task the_consumers_half_of_the_recording_outbox_still_works_on_a_stores_own_interfaces()
        {
            UnbridgedStore.IMessageOutbox outbox = new RecordingMessageOutbox();

            var batch = await outbox.CreateBatch(Substitute.For<UnbridgedStore.IStoreSession>());

            await batch.BeforeCommitAsync(CancellationToken.None);
            await batch.AfterCommitAsync(CancellationToken.None);

            ((RecordingMessageBatch)batch).Hooks.ShouldBe(["before", "after"]);
        }

        [Fact]
        public async Task the_consumers_half_of_the_recording_outbox_still_works_on_a_bridged_stores_interfaces()
        {
            BridgedStore.IMessageOutbox outbox = new RecordingMessageOutbox();

            var batch = await outbox.CreateBatch(Substitute.For<BridgedStore.IStoreSession>());

            await batch.BeforeCommitAsync(CancellationToken.None);
            await batch.AfterCommitAsync(CancellationToken.None);

            ((RecordingMessageBatch)batch).Hooks.ShouldBe(["before", "after"]);
        }

        public record Ping;

        private class BridgedBatch(IMessageOutbox outbox)
            : ProjectionBatchBase<BridgedStore.IStoreSession>(ShardExecutionMode.Continuous)
        {
            protected override IMessageOutbox Outbox => outbox;

            protected override BridgedStore.IStoreSession OpenSessionForTenant(string tenantId)
                => Substitute.For<BridgedStore.IStoreSession>();

            public override ValueTask RecordProgress(EventRange range) => ValueTask.CompletedTask;

            protected override Task ExecuteOperationsInTransactionAsync(Func<CancellationToken, Task> beforeCommit,
                CancellationToken token) => beforeCommit(token);
        }
    }
}

namespace EventTests.Projections.BridgedStore
{
    // Exactly what Polecat and Fisher declare for the bridging release

    public interface IStoreSession : IDocumentSessionOperations, IStorageOperations;

    public interface IMessageBatch : JasperFx.Events.Documents.IMessageBatch;

    public interface IMessageOutbox : JasperFx.Events.Documents.IMessageOutbox
    {
        ValueTask<IMessageBatch> CreateBatch(IStoreSession session);

        async ValueTask<JasperFx.Events.Documents.IMessageBatch> JasperFx.Events.Documents.IMessageOutbox.CreateBatch(
            IDocumentSessionOperations session) => await CreateBatch((IStoreSession)session);
    }
}

namespace EventTests.Projections.UnbridgedStore
{
    // Exactly what Polecat and Fisher declare today

    public interface IStoreSession : IDocumentSessionOperations, IStorageOperations;

    public interface IMessageBatch : IMessageSink
    {
        Task BeforeCommitAsync(CancellationToken token);
        Task AfterCommitAsync(CancellationToken token);
    }

    public interface IMessageOutbox
    {
        ValueTask<IMessageBatch> CreateBatch(IStoreSession session);
    }
}

namespace EventTests.Projections
{
    using BridgedStore;

    // Wolverine.Polecat's two Publishing classes with the Wolverine internals taken out: written against the
    // store's interfaces, with no mention of the shared ones.
    internal class WolverineShapedOutbox : BridgedStore.IMessageOutbox
    {
        public IStoreSession? Session { get; private set; }
        public WolverineShapedBatch? Batch { get; private set; }

        public async ValueTask<BridgedStore.IMessageBatch> CreateBatch(IStoreSession session)
        {
            await Task.Yield();

            Session = session;
            return Batch = new WolverineShapedBatch();
        }
    }

    internal class WolverineShapedBatch : BridgedStore.IMessageBatch
    {
        public List<Type> Published { get; } = new();
        public List<string> Hooks { get; } = new();

        public ValueTask PublishAsync<T>(T message, string tenantId)
        {
            Published.Add(typeof(T));
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
        {
            Published.Add(typeof(T));
            return ValueTask.CompletedTask;
        }

        public Task BeforeCommitAsync(CancellationToken token)
        {
            Hooks.Add("before");
            return Task.CompletedTask;
        }

        public Task AfterCommitAsync(CancellationToken token)
        {
            Hooks.Add("after");
            return Task.CompletedTask;
        }
    }
}

namespace JasperFx.Events.ComplianceTests
{
    // A consumer's half of the partials, verbatim from Polecat.Tests and Fisher.Tests. It implements the store
    // interfaces in both of their shapes, because a store goes through both, and the library half has to coexist
    // with this in each: public members here and in the library would be CS0111 in every consumer.
    public partial class RecordingMessageOutbox : global::EventTests.Projections.UnbridgedStore.IMessageOutbox,
        global::EventTests.Projections.BridgedStore.IMessageOutbox
    {
        public ValueTask<global::EventTests.Projections.UnbridgedStore.IMessageBatch> CreateBatch(
            global::EventTests.Projections.UnbridgedStore.IStoreSession session)
            => new(NewBatch());

        public ValueTask<global::EventTests.Projections.BridgedStore.IMessageBatch> CreateBatch(
            global::EventTests.Projections.BridgedStore.IStoreSession session)
            => new(NewBatch());
    }

    public partial class RecordingMessageBatch : global::EventTests.Projections.UnbridgedStore.IMessageBatch,
        global::EventTests.Projections.BridgedStore.IMessageBatch
    {
        public Task BeforeCommitAsync(CancellationToken token) => RecordBeforeCommitAsync();

        public Task AfterCommitAsync(CancellationToken token) => RecordAfterCommitAsync();
    }
}
