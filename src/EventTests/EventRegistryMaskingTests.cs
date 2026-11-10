using JasperFx.Events;
using Shouldly;

namespace EventTests;

/// <summary>
/// The masking rules lifted from Marten, Polecat and Fisher (#1043). <see cref="EventRegistry.TryMask" /> is
/// what a session-level masking builder calls for each event it selected.
/// </summary>
public class EventRegistryMaskingTests
{
    private const string Masked = "*****";

    private readonly EventRegistry theRegistry = new();

    [Fact]
    public void an_event_no_rule_matches_is_left_alone_and_reported()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<SubjectRegistered>(x => x.Subject = Masked);

        var closed = new Event<SubjectClosed>(new SubjectClosed { Reason = "resolved" });

        theRegistry.TryMask(closed).ShouldBeFalse();
        closed.Data.Reason.ShouldBe("resolved");
    }

    [Fact]
    public void a_registry_with_no_rules_masks_nothing()
    {
        theRegistry.TryMask(new Event<SubjectClosed>(new SubjectClosed())).ShouldBeFalse();
    }

    [Fact]
    public void an_action_rule_mutates_the_event_in_place()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<SubjectRegistered>(x => x.Subject = Masked);

        var data = new SubjectRegistered { Subject = "Hilda" };
        var @event = new Event<SubjectRegistered>(data);

        theRegistry.TryMask(@event).ShouldBeTrue();

        @event.Data.ShouldBeSameAs(data);
        data.Subject.ShouldBe(Masked);
    }

    [Fact]
    public void an_action_rule_against_an_interface_reaches_every_implementer()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<ISubjectEvent>(x => x.Subject = Masked);

        var registered = new Event<SubjectRegistered>(new SubjectRegistered { Subject = "Hilda" });
        var contacted = new Event<SubjectContacted>(new SubjectContacted { Subject = "Hilda" });

        theRegistry.TryMask(registered).ShouldBeTrue();
        theRegistry.TryMask(contacted).ShouldBeTrue();

        registered.Data.Subject.ShouldBe(Masked);
        contacted.Data.Subject.ShouldBe(Masked);
    }

    [Fact]
    public void a_func_rule_replaces_the_event_data()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<NoteAdded>(x => x with { Note = Masked });

        var original = new NoteAdded("caseworker", "Lives at 14 Rookery Lane");
        var @event = new Event<NoteAdded>(original);

        theRegistry.TryMask(@event).ShouldBeTrue();

        @event.Data.ShouldNotBeSameAs(original);
        @event.Data.ShouldBe(new NoteAdded("caseworker", Masked));
    }

    [Fact]
    public void a_func_rule_works_on_a_value_type_event()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<ValueEvent>(x => x with { Subject = Masked });

        var @event = new Event<ValueEvent>(new ValueEvent("Hilda"));

        theRegistry.TryMask(@event).ShouldBeTrue();

        @event.Data.Subject.ShouldBe(Masked);
    }

    [Fact]
    public void every_matching_rule_runs()
    {
        // All three stores had to fix this one: with || the first match stopped the others, and TryMask
        // still reported success with the second address unmasked.
        theRegistry.AddMaskingRuleForProtectedInformation<SubjectRelocated>(x => x.PreviousAddress = Masked);
        theRegistry.AddMaskingRuleForProtectedInformation<SubjectRelocated>(x => x.NewAddress = Masked);

        var @event = new Event<SubjectRelocated>(new SubjectRelocated
        {
            PreviousAddress = "14 Rookery Lane", NewAddress = "3 Mill Row"
        });

        theRegistry.TryMask(@event).ShouldBeTrue();

        @event.Data.PreviousAddress.ShouldBe(Masked);
        @event.Data.NewAddress.ShouldBe(Masked);
    }

    [Fact]
    public void a_later_rule_sees_what_an_earlier_rule_replaced()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<SubjectRelocated>(x => new SubjectRelocated
        {
            PreviousAddress = Masked, NewAddress = x.NewAddress
        });
        theRegistry.AddMaskingRuleForProtectedInformation<SubjectRelocated>(x => x.NewAddress = Masked);

        var @event = new Event<SubjectRelocated>(new SubjectRelocated
        {
            PreviousAddress = "14 Rookery Lane", NewAddress = "3 Mill Row"
        });

        theRegistry.TryMask(@event);

        @event.Data.PreviousAddress.ShouldBe(Masked);
        @event.Data.NewAddress.ShouldBe(Masked);
    }

    [Fact]
    public void a_func_rule_against_a_base_type_reaches_a_derived_event()
    {
        // The rule is documented to cover "event types that can be cast to T", and the Action overload
        // does. All three stores tested Event<T> here, so this matched nothing and returned false, and the
        // masking pass moved on with the protected information still in the event.
        theRegistry.AddMaskingRuleForProtectedInformation<PersonalEvent>(x => x with { Name = Masked });

        var @event = new Event<PersonRenamed>(new PersonRenamed("Hilda Ravenswood"));

        theRegistry.TryMask(@event).ShouldBeTrue();

        @event.Data.ShouldBe(new PersonRenamed(Masked));
    }

    [Fact]
    public void a_func_rule_against_an_interface_reaches_an_implementer()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<ISubjectEvent>(x =>
        {
            x.Subject = Masked;
            return x;
        });

        var @event = new Event<SubjectContacted>(new SubjectContacted { Subject = "Hilda" });

        theRegistry.TryMask(@event).ShouldBeTrue();

        @event.Data.Subject.ShouldBe(Masked);
    }

    [Fact]
    public void a_func_rule_that_returns_what_the_event_cannot_hold_fails_rather_than_skipping()
    {
        theRegistry.AddMaskingRuleForProtectedInformation<PersonalEvent>(_ => new PersonForgotten(Masked));

        var @event = new Event<PersonRenamed>(new PersonRenamed("Hilda Ravenswood"));

        var ex = Should.Throw<InvalidOperationException>(() => theRegistry.TryMask(@event));

        ex.Message.ShouldContain(nameof(PersonForgotten));
        ex.Message.ShouldContain(nameof(PersonRenamed));
        @event.Data.Name.ShouldBe("Hilda Ravenswood");
    }

    [Fact]
    public void null_rules_are_refused()
    {
        Should.Throw<ArgumentNullException>(() =>
            theRegistry.AddMaskingRuleForProtectedInformation((Action<SubjectRegistered>)null!));

        Should.Throw<ArgumentNullException>(() =>
            theRegistry.AddMaskingRuleForProtectedInformation((Func<NoteAdded, NoteAdded>)null!));
    }

    public interface ISubjectEvent
    {
        string Subject { get; set; }
    }

    public class SubjectRegistered : ISubjectEvent
    {
        public string Subject { get; set; } = "";
    }

    public class SubjectContacted : ISubjectEvent
    {
        public string Subject { get; set; } = "";
    }

    public class SubjectRelocated
    {
        public string PreviousAddress { get; set; } = "";
        public string NewAddress { get; set; } = "";
    }

    public class SubjectClosed
    {
        public string Reason { get; set; } = "";
    }

    public record NoteAdded(string Author, string Note);

    public record struct ValueEvent(string Subject);

    public abstract record PersonalEvent(string Name);

    public record PersonRenamed(string Name) : PersonalEvent(Name);

    public record PersonForgotten(string Name) : PersonalEvent(Name);
}
