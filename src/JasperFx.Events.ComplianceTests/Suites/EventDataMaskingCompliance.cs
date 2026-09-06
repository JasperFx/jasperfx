using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace JasperFx.Events.ComplianceTests;

#region Event data masking events

/// <summary>
/// Implemented by two event types so a rule registered against the interface can be shown to reach
/// both — masking rules match contravariantly on both products.
/// </summary>
public interface IComplianceSubjectEvent
{
    string Subject { get; set; }
}

public class SubjectRegistered: IComplianceSubjectEvent
{
    public string Subject { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class SubjectContacted: IComplianceSubjectEvent
{
    public string Subject { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
}

/// <summary>
/// A record, so the replacing <c>Func&lt;T,T&gt;</c> rule form has something with init-only members
/// to work against. The mutating form cannot express this.
/// </summary>
public record SubjectNoteAdded(string Author, string Note);

/// <summary>
/// Carries no masking rule at all, so it can prove that a masking pass leaves unregistered event
/// types alone rather than blanking everything in the stream.
/// </summary>
public class SubjectClosed
{
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Carries <em>two</em> rules registered against this one concrete type — not an interface and its
/// implementation, the same type twice. See
/// <c>EventDataMaskingCompliance.every_rule_against_one_concrete_type_runs</c>.
/// </summary>
public class SubjectRelocated
{
    public string PreviousAddress { get; set; } = string.Empty;
    public string NewAddress { get; set; } = string.Empty;
}

#endregion

/// <summary>
/// Batch data masking of already-stored events — the GDPR-style erasure path, where protected
/// information is rewritten in place without rewriting or replaying the stream.
/// </summary>
/// <remarks>
/// <para>
/// Two halves have to agree for this to work, and each store owns one: the <em>rules</em>, declared
/// per event type on the store's event options, and the <em>selection</em>, declared per operation
/// through <see cref="Protected.IEventDataMasking"/>. A rule with no matching selection does
/// nothing; a selection with no matching rule also does nothing. Every test here pins the
/// intersection, and — just as importantly — pins what stays untouched, because a masking bug that
/// over-applies is far worse than one that under-applies.
/// </para>
/// <para>
/// <see cref="Protected.IEventDataMasking"/> became shared in jasperfx#635, but the entry point
/// that hands one out did not: both products expose it as
/// <c>Advanced.ApplyEventDataMasking(...)</c> on store-specific advanced-operations types that
/// share no interface. So this suite costs exactly three seam members —
/// <c>ApplyEventDataMaskingAsync</c> on the fixture and the two <c>AddMaskingRule</c> overloads on
/// the config — and marten#5154's "the lift makes it portable" framing was half right: the lift
/// made the <em>vocabulary</em> shared, not the reach.
/// </para>
/// <para>
/// Deliberately not asserted: what an empty masking request does. One product's own tests pin it as
/// throwing, and nothing documents that as a cross-store contract, so encoding it here would pin an
/// implementation detail rather than a promise — the same trap the FetchLatest suite hit over
/// uncommitted appends.
/// </para>
/// </remarks>
public abstract class EventDataMaskingCompliance<TFixture, TOperations, TQuerySession>
    : EventStoreComplianceSuite<TFixture, TOperations, TQuerySession>
    where TFixture : EventStoreComplianceFixture<TOperations, TQuerySession>, new()
    where TOperations : TQuerySession, IStorageOperations
{
    private const string Masked = "*****";

    private static readonly Action<ComplianceStoreConfig> _configuration = config =>
    {
        config.SchemaName = "compliance_masking";
        config.EnableHeaders = true;

        config.AddEventType<SubjectRegistered>();
        config.AddEventType<SubjectContacted>();
        config.AddEventType<SubjectNoteAdded>();
        config.AddEventType<SubjectClosed>();
        config.AddEventType<SubjectRelocated>();

        // Contravariant: one rule against the interface reaches both implementing event types.
        config.AddMaskingRule<IComplianceSubjectEvent>(x => x.Subject = Masked);

        // A second rule against a concrete type, to prove rules compose rather than replace.
        config.AddMaskingRule<SubjectRegistered>(x => x.Email = Masked);

        // The replacing form, which is the only one a record with init-only members can use.
        config.AddMaskingRule<SubjectNoteAdded>(x => x with { Note = Masked });

        // Two rules against the SAME concrete type. See every_rule_against_one_concrete_type_runs.
        config.AddMaskingRule<SubjectRelocated>(x => x.PreviousAddress = Masked);
        config.AddMaskingRule<SubjectRelocated>(x => x.NewAddress = Masked);
    };

    /// <summary>
    /// Same rules, string stream identity, so the string <c>IncludeStream</c> overload has a store
    /// it can actually address.
    /// </summary>
    private static readonly Action<ComplianceStoreConfig> _stringConfiguration = config =>
    {
        config.SchemaName = "compliance_masking_string";
        config.StreamIdentity = StreamIdentity.AsString;
        config.EnableHeaders = true;

        config.AddEventType<SubjectRegistered>();
        config.AddEventType<SubjectContacted>();
        config.AddEventType<SubjectClosed>();

        config.AddMaskingRule<IComplianceSubjectEvent>(x => x.Subject = Masked);
    };

    protected override Action<ComplianceStoreConfig> Configuration => _configuration;

    private static object[] theSubjectEvents() =>
    [
        new SubjectRegistered { Subject = "Hilda Ravenswood", Email = "hilda@example.com" },
        new SubjectContacted { Subject = "Hilda Ravenswood", Channel = "email" },
        new SubjectNoteAdded("caseworker", "Lives at 14 Rookery Lane"),
        new SubjectRelocated { PreviousAddress = "14 Rookery Lane", NewAddress = "3 Mill Row" },
        new SubjectClosed { Reason = "resolved" }
    ];

    private async Task<Guid> aSubjectAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = OpenSession();
        EventsFor(session).StartStream(streamId, theSubjectEvents());
        await SaveChangesAsync(session);

        return streamId;
    }

    private async Task<IReadOnlyList<IEvent>> eventsForAsync(Guid streamId)
    {
        await using var query = OpenSession();
        return await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);
    }

    [Fact]
    public async Task a_rule_masks_the_matching_event_in_an_included_stream()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Cancellation);

        var events = await eventsForAsync(streamId);
        var registered = events.Select(x => x.Data).OfType<SubjectRegistered>().Single();

        registered.Subject.ShouldBe(Masked);
    }

    [Fact]
    public async Task rules_compose_rather_than_replace_one_another()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Cancellation);

        var events = await eventsForAsync(streamId);
        var registered = events.Select(x => x.Data).OfType<SubjectRegistered>().Single();

        // The interface rule and the concrete rule both ran against the same event.
        registered.Subject.ShouldBe(Masked);
        registered.Email.ShouldBe(Masked);
    }

    /// <summary>
    /// marten#5199 / polecat#422, in its sharpest form: two rules registered against the <em>same
    /// concrete type</em>, both of which must run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="rules_compose_rather_than_replace_one_another"/> already pins the bug as it was
    /// reported — an interface rule and a concrete rule matching one event, where a short-circuiting
    /// <c>||=</c> over the rule list let the first match stop every later rule from being invoked,
    /// and the operation still reported success. For a right-to-erasure feature that means protected
    /// information silently survives a masking pass.
    /// </para>
    /// <para>
    /// This fact closes the neighbouring hole: a store that keyed its rules by event type — a
    /// dictionary rather than a list — passes the interface/concrete version, because those are two
    /// different keys, and silently drops one of the two rules here. Same failure, same silence,
    /// different mistake.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task every_rule_against_one_concrete_type_runs()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Cancellation);

        var events = await eventsForAsync(streamId);
        var relocated = events.Select(x => x.Data).OfType<SubjectRelocated>().Single();

        relocated.PreviousAddress.ShouldBe(Masked);
        relocated.NewAddress.ShouldBe(Masked);
    }

    [Fact]
    public async Task a_rule_registered_against_an_interface_reaches_every_implementing_event()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Cancellation);

        var events = await eventsForAsync(streamId);

        events.Select(x => x.Data).OfType<SubjectRegistered>().Single().Subject.ShouldBe(Masked);
        events.Select(x => x.Data).OfType<SubjectContacted>().Single().Subject.ShouldBe(Masked);
    }

    [Fact]
    public async Task the_replacing_rule_form_masks_a_record()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Cancellation);

        var events = await eventsForAsync(streamId);
        var note = events.Select(x => x.Data).OfType<SubjectNoteAdded>().Single();

        note.Note.ShouldBe(Masked);

        // Replaced, not blanked: members the rule did not touch survive.
        note.Author.ShouldBe("caseworker");
    }

    [Fact]
    public async Task an_event_type_with_no_rule_is_left_alone()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Cancellation);

        var events = await eventsForAsync(streamId);
        var closed = events.Select(x => x.Data).OfType<SubjectClosed>().Single();

        closed.Reason.ShouldBe("resolved");
    }

    [Fact]
    public async Task a_stream_that_was_not_included_is_untouched()
    {
        var included = await aSubjectAsync();
        var excluded = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(included), Cancellation);

        var events = await eventsForAsync(excluded);
        var registered = events.Select(x => x.Data).OfType<SubjectRegistered>().Single();

        registered.Subject.ShouldBe("Hilda Ravenswood");
        registered.Email.ShouldBe("hilda@example.com");
    }

    [Fact]
    public async Task masking_does_not_add_events_or_change_the_stream_version()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Cancellation);

        await using var query = OpenSession();

        var state = await EventsFor(query).FetchStreamStateAsync(streamId, Cancellation);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(5);

        var events = await EventsFor(query).FetchStreamAsync(streamId, token: Cancellation);
        events.Count.ShouldBe(5);
        events.Select(x => x.Version).ShouldBe(new long[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task a_stream_filter_narrows_the_masking_to_matching_events()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId, e => e.Data is SubjectContacted), Cancellation);

        var events = await eventsForAsync(streamId);

        events.Select(x => x.Data).OfType<SubjectContacted>().Single().Subject.ShouldBe(Masked);

        // The filter excluded this one even though a rule matches its type.
        events.Select(x => x.Data).OfType<SubjectRegistered>().Single()
            .Subject.ShouldBe("Hilda Ravenswood");
    }

    [Fact]
    public async Task added_headers_land_on_the_masked_events_only()
    {
        var streamId = await aSubjectAsync();

        await theFixture.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId, e => e.Data is SubjectContacted).AddHeader("erasure", "case-17"),
            Cancellation);

        var events = await eventsForAsync(streamId);

        var contacted = events.Single(x => x.Data is SubjectContacted);

        // Through GetHeader and compared as a string, following EventMetadataCompliance: header
        // values do not round-trip as the same runtime type on both stores (one rehydrates them as
        // the CLR type, the other as a JsonElement), and that divergence is out of scope here.
        contacted.GetHeader("erasure").ShouldNotBeNull();
        contacted.GetHeader("erasure")!.ToString().ShouldBe("case-17");

        // Every other event in the same stream is left without the header.
        foreach (var @event in events.Where(x => x.Data is not SubjectContacted))
        {
            @event.GetHeader("erasure").ShouldBeNull();
        }
    }

    [Fact]
    public async Task a_string_identified_stream_masks_the_same_way()
    {
        await theFixture.ConfigureAsync(_stringConfiguration);

        var streamKey = $"subject/{Guid.NewGuid():N}";

        await using (var session = OpenSession())
        {
            EventsFor(session).StartStream(streamKey,
                new SubjectRegistered { Subject = "Hilda Ravenswood", Email = "hilda@example.com" },
                new SubjectClosed { Reason = "resolved" });
            await SaveChangesAsync(session);
        }

        await theFixture.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamKey), Cancellation);

        await using var query = OpenSession();
        var events = await EventsFor(query).FetchStreamAsync(streamKey, token: Cancellation);

        events.Select(x => x.Data).OfType<SubjectRegistered>().Single().Subject.ShouldBe(Masked);
        events.Select(x => x.Data).OfType<SubjectClosed>().Single().Reason.ShouldBe("resolved");
    }
}
