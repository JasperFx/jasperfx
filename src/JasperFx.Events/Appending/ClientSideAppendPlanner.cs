using JasperFx.Core;
using JasperFx.MultiTenancy;

namespace JasperFx.Events.Appending;

/// <summary>
///     What a stream action asks of the database, before anyone has looked at the stream.
/// </summary>
public enum StreamAppendWork
{
    /// <summary>
    ///     No events, and nobody asked for the version to be checked.
    /// </summary>
    Nothing,

    /// <summary>
    ///     No events, but <see cref="StreamAction.AlwaysEnforceConsistency" /> is set and there is an
    ///     expected version: "I read this stream, decided to write nothing, and that is only sound if it
    ///     has not moved."
    /// </summary>
    VerifyVersion,

    /// <summary>
    ///     Events for a stream that must be new.
    /// </summary>
    Start,

    /// <summary>
    ///     Events for a stream that may or may not exist yet.
    /// </summary>
    Append,

    /// <summary>
    ///     Events under a tenant other than the action's own, on a store where that is another stream.
    /// </summary>
    MixedTenants
}

/// <summary>
///     How a stream's expected version compares with the stream.
/// </summary>
public enum ExpectedVersionCheck
{
    /// <summary>
    ///     Nothing is expected, or what is expected is not a claim. See <see cref="AppendOrigin" />.
    /// </summary>
    None,

    /// <summary>
    ///     The stream is at the expected version. A stream that does not exist is at version 0.
    /// </summary>
    Matches,

    /// <summary>
    ///     The stream is somewhere else.
    /// </summary>
    Differs
}

/// <summary>
///     What the plan does with a stream.
/// </summary>
public enum StreamAppendVerdict
{
    /// <summary>
    ///     Nothing to do.
    /// </summary>
    Skip,

    /// <summary>
    ///     Nothing to write, and the version check passed.
    /// </summary>
    Verified,

    /// <summary>
    ///     Insert the stream row, then the events.
    /// </summary>
    InsertStream,

    /// <summary>
    ///     Move the stream row's version on, then insert the events.
    /// </summary>
    UpdateStream,

    /// <summary>
    ///     The id is in use.
    /// </summary>
    RefuseCollision,

    /// <summary>
    ///     The stream is archived.
    /// </summary>
    RefuseArchived,

    /// <summary>
    ///     The stream is not at the expected version.
    /// </summary>
    RefuseUnexpectedVersion,

    /// <summary>
    ///     The action holds events of another tenant.
    /// </summary>
    RefuseMixedTenants,

    /// <summary>
    ///     The store did not read a stream it had to. A defect in the store rather than a concurrency
    ///     outcome.
    /// </summary>
    RefuseUnreadRow
}

/// <summary>
///     One row of <see cref="ClientSideAppendPlanner.Rules" />: every combination of the listed values gets
///     this verdict.
/// </summary>
/// <param name="Work">What the action asks for.</param>
/// <param name="Rows">What the read found.</param>
/// <param name="Expected">How the expected version compares.</param>
/// <param name="Verdict">What the plan does.</param>
/// <param name="Why">The reason, which is what a row in a table can say that a branch cannot.</param>
public sealed record StreamAppendRule(
    StreamAppendWork[] Work,
    StreamRowState[] Rows,
    ExpectedVersionCheck[] Expected,
    StreamAppendVerdict Verdict,
    string Why)
{
    /// <summary>
    ///     Does this row cover the combination?
    /// </summary>
    public bool Covers(StreamAppendWork work, StreamRowState row, ExpectedVersionCheck expected)
        => Work.Contains(work) && Rows.Contains(row) && Expected.Contains(expected);
}

/// <summary>
///     Decides what happens to each stream of a unit of work when the client numbers the events itself,
///     as a table of rules over what the stream actions ask for and what the store found.
/// </summary>
/// <remarks>
///     <para>
///     <b>Lifted from Polecat's <c>ProcessStreamsClosedShapeAsync</c> and Fisher's <c>AppendPlanner</c>
///     (jasperfx#1051)</b>, which agreed on most of this and drifted on the rest; a wrong row here silently
///     corrupts stream versions, so the rules are data and a test checks them against every combination.
///     Marten is not a consumer: its unnest and <c>mt_quick_append</c> paths let the database number events.
///     </para>
///     <para>
///     <b>This is a pure function of its inputs.</b> A store reads the rows, under whatever makes that safe,
///     and passes them in. <see cref="Plan" /> reads the actions and changes nothing, and neither it nor
///     <see cref="ClientSideAppendPlan.Apply" /> does any I/O, so what a read is worth is the store's
///     business and stays visible there. Fisher reads inside <c>BEGIN IMMEDIATE</c>, with the only writer
///     the file allows. Polecat reads under <c>UPDLOCK, HOLDLOCK</c>, which holds the row, or the gap where
///     it would be, until commit. A store with neither must not plan from a read it is not holding: the
///     table will happily reach a confident answer from a stale row, and only the stream-row write's
///     <c>and version = @expected</c> and the primary key stand behind it. Both of those stay in place.
///     </para>
///     <para>
///     <b>The expected version here has one meaning: the version of the stream before these events.</b>
///     That is <see cref="StreamAction.ExpectedVersionOnServer" />, whose every writer already means the
///     starting version. <c>Append(stream, expectedVersion, ...)</c> takes the version <em>after</em> the
///     append (jasperfx#1047) and <see cref="EventOperationsBase" /> converts it once, so this class must not
///     subtract the pending events a second time.
///     </para>
///     <para>
///     <b>What the planner cannot see.</b> It sees the actions a store hands it, and that is where its
///     protection ends. Marten's and Polecat's <c>TryFindStream</c> is tenant-blind, so a second
///     <c>ForTenant</c> append to one id lands on the first tenant's action, and <c>AddEvent</c> restamps
///     the event, so by planning time nothing distinguishes it. Fisher's <c>StartStream</c> replaces a
///     pending action in its dictionary, so the dropped events are simply not among the inputs. Both have
///     to be fixed where they happen. Two actions that do reach the planner for one stream are folded in
///     order: see <see cref="Plan" />.
///     </para>
/// </remarks>
public static class ClientSideAppendPlanner
{
    private static readonly StreamRowState[] AnyRow =
    [
        StreamRowState.NotRead, StreamRowState.Missing, StreamRowState.Live, StreamRowState.Archived
    ];

    private static readonly StreamRowState[] AnyReadRow =
        [StreamRowState.Missing, StreamRowState.Live, StreamRowState.Archived];

    private static readonly ExpectedVersionCheck[] AnyExpected =
        [ExpectedVersionCheck.None, ExpectedVersionCheck.Matches, ExpectedVersionCheck.Differs];

    private static readonly ExpectedVersionCheck[] Compatible =
        [ExpectedVersionCheck.None, ExpectedVersionCheck.Matches];

    private static readonly ExpectedVersionCheck[] Contradicted = [ExpectedVersionCheck.Differs];

    /// <summary>
    ///     The decision table. Every combination of <see cref="StreamAppendWork" />,
    ///     <see cref="StreamRowState" /> and <see cref="ExpectedVersionCheck" /> is covered by exactly one
    ///     row, which <c>ClientSideAppendPlannerTests</c> enumerates rather than trusts.
    /// </summary>
    public static IReadOnlyList<StreamAppendRule> Rules { get; } =
    [
        new([StreamAppendWork.Nothing], AnyRow, AnyExpected, StreamAppendVerdict.Skip,
            "No events and no check asked for, so a stream fetched and left alone writes nothing"),

        new([StreamAppendWork.MixedTenants], AnyRow, AnyExpected, StreamAppendVerdict.RefuseMixedTenants,
            "One stream row cannot hold two tenants' events: stamping the action's tenant would move an event, and writing the event's own would leave it out of its stream"),

        // ---- no events, but the caller's decision rested on the version ----

        new([StreamAppendWork.VerifyVersion], [StreamRowState.NotRead], AnyExpected, StreamAppendVerdict.RefuseUnreadRow,
            "There is nothing to compare the expected version with"),

        new([StreamAppendWork.VerifyVersion], AnyReadRow, Compatible, StreamAppendVerdict.Verified,
            "The stream has not moved. A stream that does not exist yet was fetched at 0 and reads back as 0, and an archived stream's version is still its version"),

        new([StreamAppendWork.VerifyVersion], AnyReadRow, Contradicted, StreamAppendVerdict.RefuseUnexpectedVersion,
            "The stream moved after it was read, so the decision to write nothing is not sound. Archiving does not excuse it: nothing is being appended"),

        // ---- Start ----

        new([StreamAppendWork.Start], [StreamRowState.Live, StreamRowState.Archived], AnyExpected, StreamAppendVerdict.RefuseCollision,
            "An archived id is still an id in use, so this is a collision and not an archived-stream refusal: what the caller needs is a different id, not UnArchiveStream"),

        new([StreamAppendWork.Start], [StreamRowState.Missing], Compatible, StreamAppendVerdict.InsertStream,
            "A new stream, numbered from 1"),

        new([StreamAppendWork.Start], [StreamRowState.NotRead], Compatible, StreamAppendVerdict.InsertStream,
            "A store may skip the read for a Start, as Polecat does, because the stream row's primary key refuses a collision at write time. Its exception transform stays"),

        new([StreamAppendWork.Start], [StreamRowState.Missing, StreamRowState.NotRead], Contradicted, StreamAppendVerdict.RefuseUnexpectedVersion,
            "A Start is a claim to be at version 0, so a different expected version contradicts the action itself. Polecat and Fisher both used to ignore it"),

        // ---- Append ----

        new([StreamAppendWork.Append], [StreamRowState.NotRead], AnyExpected, StreamAppendVerdict.RefuseUnreadRow,
            "An append is numbered from the current version, and a store that forgot to read this stream must not restart it at 1"),

        new([StreamAppendWork.Append], [StreamRowState.Missing], Compatible, StreamAppendVerdict.InsertStream,
            "Append is not an assertion that the stream exists: it creates a stream that does not"),

        new([StreamAppendWork.Append], [StreamRowState.Missing], Contradicted, StreamAppendVerdict.RefuseUnexpectedVersion,
            "An expected version above 0 claims the stream exists, and a missing row contradicts that as plainly as another version would. Dropping this restarts a tombstoned stream mid-life (fisher#378)"),

        new([StreamAppendWork.Append], [StreamRowState.Archived], AnyExpected, StreamAppendVerdict.RefuseArchived,
            "Archiving is not a soft delete you can keep writing through. Ahead of the version check because 'this stream is closed' is the more specific answer"),

        new([StreamAppendWork.Append], [StreamRowState.Live], Compatible, StreamAppendVerdict.UpdateStream,
            "The ordinary append, numbered from the version just read"),

        new([StreamAppendWork.Append], [StreamRowState.Live], Contradicted, StreamAppendVerdict.RefuseUnexpectedVersion,
            "Optimistic concurrency")
    ];

    /// <summary>
    ///     Look a combination up in <see cref="Rules" />.
    /// </summary>
    public static StreamAppendRule Decide(StreamAppendWork work, StreamRowState row, ExpectedVersionCheck expected)
    {
        foreach (var rule in Rules)
        {
            if (rule.Covers(work, row, expected)) return rule;
        }

        throw new InvalidOperationException($"No append rule covers {work}, {row}, {expected}");
    }

    /// <summary>
    ///     What an action asks for, which is also how a store tells which streams it has to read: all of
    ///     them except <see cref="StreamAppendWork.Nothing" />.
    /// </summary>
    public static StreamAppendWork WorkFor(StreamAction stream, TenancyStyle tenancy)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.Events.Count == 0)
        {
            return stream is { AlwaysEnforceConsistency: true, ExpectedVersionOnServer: not null }
                ? StreamAppendWork.VerifyVersion
                : StreamAppendWork.Nothing;
        }

        if (tenancy == TenancyStyle.Conjoined && holdsAnotherTenant(stream))
        {
            return StreamAppendWork.MixedTenants;
        }

        return stream.ActionType == StreamActionType.Start ? StreamAppendWork.Start : StreamAppendWork.Append;
    }

    /// <summary>
    ///     Decide what happens to each stream, changing nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Several actions for one stream are folded in order</b>, each planned against the state the one
    ///     before leaves behind rather than against the row. Two slices raising events onto one stream, or a
    ///     store that does not merge an <c>Append</c> with a later <c>StartStream</c>, used to number both
    ///     from the same version. Folding makes the second a continuation, and makes a <c>Start</c> after
    ///     anything a collision here instead of a primary key violation on one store and a
    ///     lost action on another. Nothing is merged or dropped: every action comes back as a step.
    ///     </para>
    ///     <para>
    ///     <b>A refused plan is a whole plan</b>, with every stream still decided, and because nothing has
    ///     been changed yet, no action is left half-numbered for a retry to find. Fisher numbered each stream
    ///     as it went and could throw on a later one.
    ///     </para>
    /// </remarks>
    /// <param name="streams">The unit of work's actions, in order.</param>
    /// <param name="rowFor">
    ///     The stream's row, scoped to the action's own tenant. Called once per stream, and never for
    ///     <see cref="StreamAppendWork.Nothing" />. A store answers
    ///     <see cref="StreamRowSnapshot.NotRead" /> for a <c>Start</c> it chose not to read.
    /// </param>
    /// <param name="context">Everything else the plan depends on.</param>
    public static ClientSideAppendPlan Plan(IReadOnlyCollection<StreamAction> streams,
        Func<StreamAction, StreamRowSnapshot> rowFor, ClientSideAppendContext context)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(rowFor);
        ArgumentNullException.ThrowIfNull(context);

        var steps = new List<StreamAppendStep>(streams.Count);
        var read = new Dictionary<(object Id, string? Tenant), StreamRowSnapshot>();
        var folded = new Dictionary<(object Id, string? Tenant), StreamRowSnapshot>();
        var sequencesNeeded = 0;

        foreach (var stream in streams)
        {
            var work = WorkFor(stream, context.Tenancy);
            var key = (streamId(stream), context.Tenancy == TenancyStyle.Conjoined ? stream.TenantId : null);

            var row = StreamRowSnapshot.NotRead;
            if (work != StreamAppendWork.Nothing)
            {
                if (!read.TryGetValue(key, out row)) row = read[key] = rowFor(stream);

                // A version check is about the row as it was read: this unit of work moving the stream
                // itself is not the stream moving under the caller's decision.
                if (work != StreamAppendWork.VerifyVersion && folded.TryGetValue(key, out var advanced))
                {
                    row = advanced;
                }
            }

            var expected = compare(stream.ExpectedVersionOnServer, row.Version, context.Origin);
            var rule = Decide(work, row.State, expected);

            var step = new StreamAppendStep(stream, rule, row.Version);
            steps.Add(step);

            if (step.IsWrite)
            {
                folded[key] = StreamRowSnapshot.Live(step.EndingVersion);
                sequencesNeeded += stream.Events.Count;
            }
        }

        var plan = new ClientSideAppendPlan(steps, context);

        // Only a plan that will write can be short of sequences, and a refused one reports its refusal.
        if (context.AppendMode == EventAppendMode.Rich && plan.Refusal is null
            && (context.ReservedSequences?.Count ?? 0) < sequencesNeeded)
        {
            throw new InvalidOperationException(
                $"EventAppendMode.Rich promises inline projections every event's sequence, but this plan writes {sequencesNeeded} events with {context.ReservedSequences?.Count ?? 0} reserved sequences");
        }

        return plan;
    }

    /// <summary>
    ///     Number and stamp events ahead of the real plan, for a store whose inline projections run before
    ///     its write lock is taken, as Fisher's do. A projection has to fold events that know their versions.
    /// </summary>
    /// <remarks>
    ///     Deliberately guards nothing and refuses nothing: the rows behind this were read outside the lock,
    ///     so they are only good enough to give a projection something to read. The authoritative read, the
    ///     table and the final numbering all still happen in <see cref="Plan" /> under the lock, and seeding
    ///     that from this is exactly the race it exists to close. It leaves
    ///     <see cref="StreamAction.ExpectedVersionOnServer" /> alone for the same reason: setting it here
    ///     would make the real check compare a value with itself.
    /// </remarks>
    public static void NumberAhead(IReadOnlyCollection<StreamAction> streams,
        Func<StreamAction, StreamRowSnapshot> rowFor, ClientSideAppendContext context)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(rowFor);
        ArgumentNullException.ThrowIfNull(context);

        var folded = new Dictionary<(object Id, string? Tenant), long>();

        foreach (var stream in streams)
        {
            if (stream.Events.Count == 0) continue;

            var key = (streamId(stream), context.Tenancy == TenancyStyle.Conjoined ? stream.TenantId : null);
            if (!folded.TryGetValue(key, out var version)) version = rowFor(stream).Version;

            Stamp(stream, version, context);
            folded[key] = stream.Version;
        }
    }

    internal static object streamId(StreamAction stream) => stream.Key is not null ? stream.Key : stream.Id;

    /// <summary>
    ///     Number a stream's events from <paramref name="startingVersion" /> and stamp everything else a
    ///     client-side plan is responsible for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Metadata is gated on the store's own <c>Enable*</c> switches, each one separately, because a
    ///     disabled column is not written at all. Stamping the envelope anyway reports metadata to an inline
    ///     projection that a rebuild of the same events will not find. Polecat stamped regardless and Fisher
    ///     gated, and Fisher's is the one that keeps the two views equal (jasperfx#1051).
    ///     </para>
    ///     <para>
    ///     Everything an event already carries is left alone, which is what makes a transient retry
    ///     re-entering this harmless, and is what lets a caller override a timestamp. Polecat overwrote the
    ///     timestamp unconditionally. A session header does not replace an event's own header of the same
    ///     name, for the same reason its correlation id does not replace the event's; Fisher's
    ///     <c>SetHeader</c> did, against its own summary.
    ///     </para>
    /// </remarks>
    internal static void Stamp(StreamAction stream, long startingVersion, ClientSideAppendContext context)
    {
        var metadata = context.Metadata;
        var version = startingVersion;

        foreach (var @event in stream.Events)
        {
            @event.Version = ++version;

            if (@event.Id == Guid.Empty) @event.Id = CombGuidIdGeneration.NewGuid();
            if (@event.TenantId.IsEmpty()) @event.TenantId = stream.TenantId;
            if (@event.Timestamp == default) @event.Timestamp = context.Timestamp;

            if (metadata.CorrelationIdEnabled) @event.CorrelationId ??= metadata.CorrelationId;
            if (metadata.CausationIdEnabled) @event.CausationId ??= metadata.CausationId;
            if (metadata.UserNameEnabled) @event.UserName ??= metadata.CurrentUserName;

            if (!metadata.HeadersEnabled || !(metadata.Headers?.Count > 0)) continue;

            @event.Headers ??= new Dictionary<string, object>();
            foreach (var header in metadata.Headers) @event.Headers.TryAdd(header.Key, header.Value);
        }

        stream.Version = version;
    }

    private static ExpectedVersionCheck compare(long? expected, long current, AppendOrigin origin)
    {
        if (origin == AppendOrigin.RaisedByProjection || expected is null) return ExpectedVersionCheck.None;

        return expected == current ? ExpectedVersionCheck.Matches : ExpectedVersionCheck.Differs;
    }

    private static bool holdsAnotherTenant(StreamAction stream)
    {
        if (stream.TenantId.IsEmpty()) return false;

        foreach (var @event in stream.Events)
        {
            if (@event.TenantId.IsNotEmpty() && @event.TenantId != stream.TenantId) return true;
        }

        return false;
    }
}
