using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Events.Documents;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

/// <summary>
/// Records every commit it is told about, so a suite can assert on what the store handed it.
/// </summary>
/// <remarks>
/// It keeps the <see cref="IDocumentChangeSet" /> itself rather than copying the three collections
/// out of it, and that is deliberate: the contract promises the collections are snapshots, so a
/// change set read after the session has moved on must still answer correctly. A store that handed
/// out a live view of its unit of work — which is what Marten's <c>IChangeSet</c> natively is —
/// fails <see cref="DocumentCommitListenerCompliance{TFixture}.the_change_set_survives_the_session_moving_on" />
/// rather than passing quietly.
/// </remarks>
public class RecordingCommitListener : IDocumentCommitListener
{
    private readonly List<(IDocumentSessionOperations Session, IDocumentChangeSet Commit)> _commits = new();

    public IReadOnlyList<(IDocumentSessionOperations Session, IDocumentChangeSet Commit)> Commits
    {
        get
        {
            lock (_commits)
            {
                return _commits.ToArray();
            }
        }
    }

    public Task AfterCommitAsync(
        IDocumentSessionOperations session,
        IDocumentChangeSet commit,
        CancellationToken token)
    {
        lock (_commits)
        {
            _commits.Add((session, commit));
        }

        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_commits)
        {
            _commits.Clear();
        }
    }
}

/// <summary>
/// <see cref="IDocumentCommitListener" /> — the post-commit session hook and the change set it
/// receives (jasperfx#679).
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, but for a different reason than the two event-capable document suites: it needs nothing
/// but documents, so any store implementing the jasperfx#647 contract can enroll. What it needs
/// instead is that the fixture <em>replay</em> <see cref="DocumentComplianceConfig.CommitListeners" />
/// onto the store's own listener collection when it builds the store. A fixture that ignores that
/// member fails every fact here rather than skipping them, which is correct: a listener that was
/// never registered and a store that never invokes listeners are the same observable failure.
/// </para>
/// <para>
/// ⚠️ <strong>This suite is the only thing standing between the contract and a silent no-op.</strong>
/// Neither <see cref="IDocumentCommitListener" /> nor <see cref="IDocumentChangeSet" /> has a
/// default implementation, so unlike jasperfx#669 there is no throwing default for a near-miss to
/// bind to — a store that declares the interfaces wrongly gets a compile error. But a store that
/// declares them perfectly and never calls the listener compiles clean and passes every other suite
/// in this library. The wiring is not visible to the compiler at any point.
/// </para>
/// <para>
/// <strong>Two behaviors are deliberately NOT asserted, because the contract permits both answers
/// and a suite that picked one would fail a correct store</strong> — the jasperfx#672 rule applied to
/// firing semantics rather than to configuration:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <strong>The empty unit of work.</strong> A <c>SaveChangesAsync</c> with nothing enlisted need not
/// raise a commit that wrote nothing; Fisher short-circuits, and Marten's matching behavior was
/// never stated. Asserting a fire would fail Fisher, and asserting no fire would pin an
/// unspecified detail on the others.
/// </description>
/// </item>
/// <item>
/// <description>
/// <strong>A session enlisted in a caller's ambient transaction.</strong> Fisher deliberately does
/// not fire for one, since the enclosing transaction rather than <c>SaveChangesAsync</c> is what
/// makes the data durable; Marten fires unconditionally. It is also unreachable from here — every
/// product spells enlistment on its own <c>SessionOptions</c>, which
/// <see cref="IDocumentSessionFactory" /> does not expose — so it belongs in each store's own tests.
/// </description>
/// </item>
/// </list>
/// </remarks>
public abstract class DocumentCommitListenerCompliance<TFixture> : DocumentStorageComplianceSuite<TFixture>
    where TFixture : DocumentStorageComplianceFixture, new()
{
    // Static because the configuration delegate has to be a stable instance for the fixture to skip
    // redundant rebuilds, and the suite has to hold the very instances it registered in order to read
    // them back. Cleared per test in InitializeAsync -- xUnit runs the methods of one class
    // sequentially, so the recordings never overlap.
    private static readonly RecordingCommitListener _listener = new();
    private static readonly RecordingCommitListener _second = new();

    private static readonly Action<DocumentComplianceConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_documents";

        config.AddDocumentType<ComplianceWidget>();

        // Two, so that "the store invoked a listener" cannot be mistaken for "the store invoked
        // every listener" -- a store forwarding only the first registration passes with one.
        config.AddCommitListener(_listener);
        config.AddCommitListener(_second);
    };

    protected override Action<DocumentComplianceConfig> Configuration => _configuration;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        _listener.Clear();
        _second.Clear();
    }

    [Fact]
    public async Task a_listener_fires_after_a_successful_commit()
    {
        await PersistAsync(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Flange" });

        // The whole point of the suite. A store that declares both interfaces and never wires them
        // up reaches this line with an empty list and no compile error anywhere behind it.
        _listener.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task every_registered_listener_is_invoked()
    {
        await PersistAsync(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Nacelle" });

        _listener.Commits.Count.ShouldBe(1);
        _second.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task each_commit_raises_its_own_callback()
    {
        await using var session = LightweightSession();

        session.Store(new ComplianceWidget { Id = Guid.NewGuid(), Name = "First" });
        await session.SaveChangesAsync(Cancellation);

        session.Store(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Second" });
        await session.SaveChangesAsync(Cancellation);

        // Two commits, two callbacks -- and specifically not one callback carrying both, which is
        // what a store accumulating across the session's lifetime would produce.
        _listener.Commits.Count.ShouldBe(2);
        _listener.Commits[0].Commit.Inserted.Concat(_listener.Commits[0].Commit.Updated)
            .OfType<ComplianceWidget>().Select(x => x.Name).ShouldBe(new[] { "First" });
        _listener.Commits[1].Commit.Inserted.Concat(_listener.Commits[1].Commit.Updated)
            .OfType<ComplianceWidget>().Select(x => x.Name).ShouldBe(new[] { "Second" });
    }

    [Fact]
    public async Task the_change_set_carries_the_written_document()
    {
        var id = Guid.NewGuid();
        await PersistAsync(new ComplianceWidget { Id = id, Name = "Strut", Weight = 7 });

        var commit = _listener.Commits.ShouldHaveSingleItem().Commit;

        // Inserted or Updated -- which of the two a first write lands in is the store's own
        // determination and is not held to a definition here. What is pinned is that it is in
        // exactly one of them, and that the document itself is what arrives.
        var written = commit.Inserted.Concat(commit.Updated).ShouldHaveSingleItem();

        var widget = written.ShouldBeOfType<ComplianceWidget>();
        widget.Id.ShouldBe(id);
        widget.Name.ShouldBe("Strut");
        widget.Weight.ShouldBe(7);

        commit.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_document_written_twice_is_reported_by_both_commits()
    {
        var id = Guid.NewGuid();

        await PersistAsync(new ComplianceWidget { Id = id, Name = "Original" });
        _listener.Clear();

        await PersistAsync(new ComplianceWidget { Id = id, Name = "Revised" });

        var commit = _listener.Commits.ShouldHaveSingleItem().Commit;
        var written = commit.Inserted.Concat(commit.Updated).ShouldHaveSingleItem();

        written.ShouldBeOfType<ComplianceWidget>().Name.ShouldBe("Revised");
    }

    [Fact]
    public async Task a_deleted_document_is_reported_by_type_and_identity()
    {
        var id = Guid.NewGuid();
        var widget = new ComplianceWidget { Id = id, Name = "Doomed" };

        await PersistAsync(widget);
        _listener.Clear();

        await using (var session = LightweightSession())
        {
            session.Delete(widget);
            await session.SaveChangesAsync(Cancellation);
        }

        var commit = _listener.Commits.ShouldHaveSingleItem().Commit;
        var deletion = commit.Deleted.ShouldHaveSingleItem();

        deletion.DocumentType.ShouldBe(typeof(ComplianceWidget));

        // Deliberately not asserting the identity's runtime type beyond equality: a store is free to
        // hand back the raw Guid or its own wrapped identity, and the contract only says this names
        // the document that went.
        deletion.Id.ShouldBe(id);
    }

    [Fact]
    public async Task the_listener_does_not_fire_for_work_that_was_never_committed()
    {
        await using (var session = LightweightSession())
        {
            session.Store(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Abandoned" });

            // Disposed without SaveChangesAsync. This is the cheap version of the failed-commit
            // fact below and catches the same class of mistake -- a store that raises the callback
            // from Store(), or from a finally around the commit, fires here.
        }

        _listener.Commits.ShouldBeEmpty();
    }

    /// <remarks>
    /// <para>
    /// Written as a biconditional rather than as "a failed commit does not fire", because there is
    /// no way through <see cref="IDocumentSessionFactory" /> to make a commit fail that every store
    /// is obliged to honor. A pre-cancelled token is the closest the contract comes, and a store
    /// that completes the commit anyway is not thereby wrong.
    /// </para>
    /// <para>
    /// So the fact asserts the invariant the contract actually states — the callback happens if and
    /// only if the commit succeeded — which is non-vacuous on both branches. A store that fires on a
    /// rolled-back transaction fails it, and so does a store that swallows the cancellation and then
    /// fails to report the commit it did perform. Neither branch is a free pass.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task the_listener_fires_if_and_only_if_the_commit_succeeded()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await using var session = LightweightSession();
        session.Store(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Cancelled" });

        var committed = true;
        try
        {
            await session.SaveChangesAsync(source.Token);
        }
        catch (OperationCanceledException)
        {
            committed = false;
        }

        _listener.Commits.Count.ShouldBe(committed ? 1 : 0);
    }

    [Fact]
    public async Task the_committing_session_is_handed_to_the_listener()
    {
        await PersistAsync(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Cowling" });

        var session = _listener.Commits.ShouldHaveSingleItem().Session;

        // Not asserted to be the same instance the suite opened: a store is free to hand over a
        // wrapper. What the contract promises is that a usable session arrives, so the listener can
        // query or enlist further work from it.
        session.ShouldNotBeNull();
    }

    /// <remarks>
    /// The snapshot requirement, and the one fact here that a store can fail while doing everything
    /// else right. Marten's <c>IChangeSet</c> <em>is</em> the session's live unit of work and is
    /// reset immediately after the listener loop — so a store forwarding it unmaterialized answers
    /// correctly inside the callback and empty by the time anyone reads it again. That is why
    /// <see cref="IDocumentChangeSet" /> is declared in terms of <see cref="IReadOnlyList{T}" />
    /// rather than <see cref="IEnumerable{T}" />, and why the shared contract needs no counterpart
    /// to Marten's <c>IChangeSet.Clone()</c>.
    /// </remarks>
    [Fact]
    public async Task the_change_set_survives_the_session_moving_on()
    {
        var id = Guid.NewGuid();

        await using var session = LightweightSession();
        session.Store(new ComplianceWidget { Id = id, Name = "Persistent" });
        await session.SaveChangesAsync(Cancellation);

        var commit = _listener.Commits.ShouldHaveSingleItem().Commit;

        // Move the session well past the commit the listener was told about.
        session.Store(new ComplianceWidget { Id = Guid.NewGuid(), Name = "Later" });
        await session.SaveChangesAsync(Cancellation);

        var written = commit.Inserted.Concat(commit.Updated).ShouldHaveSingleItem();
        written.ShouldBeOfType<ComplianceWidget>().Id.ShouldBe(id);
    }
}
