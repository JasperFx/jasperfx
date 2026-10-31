using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

/// <summary>
/// The tag registration lifted from Marten, Polecat and Fisher (#1043). Marten's was the strictest, so its
/// guards are what the base class has, and the default table suffix is the one thing that stays per store.
/// </summary>
public class EventRegistryTagTypeTests
{
    private readonly EventRegistry theRegistry = new();

    [Fact]
    public void a_registered_tag_type_can_be_found()
    {
        var registration = theRegistry.RegisterTagType<EnrolleeId>();

        registration.TagType.ShouldBe(typeof(EnrolleeId));
        registration.SimpleType.ShouldBe(typeof(Guid));

        theRegistry.FindTagType(typeof(EnrolleeId)).ShouldBeSameAs(registration);
        theRegistry.TagTypes.ShouldBe([registration]);
    }

    [Fact]
    public void an_unregistered_tag_type_is_not_found()
    {
        theRegistry.FindTagType(typeof(EnrolleeId)).ShouldBeNull();
        theRegistry.TagTypes.ShouldBeEmpty();
    }

    [Fact]
    public void the_default_suffix_is_the_table_alias()
    {
        theRegistry.RegisterTagType<EnrolleeId>().TableSuffix.ShouldBe("enrollee_id");
        theRegistry.RegisterTagType<EUTaxId, string>(x => x.Value).TableSuffix.ShouldBe("eu_tax_id");
    }

    [Fact]
    public void an_explicit_suffix_is_kept()
    {
        theRegistry.RegisterTagType<EnrolleeId>("enrollee").TableSuffix.ShouldBe("enrollee");
        theRegistry.RegisterTagType<EUTaxId, string>(x => x.Value, "tax").TableSuffix.ShouldBe("tax");
    }

    [Fact]
    public void a_store_keeps_its_own_default_suffix_on_every_overload()
    {
        // Fisher's is per-character snake case, which names its deployed fi_event_tag_* tables. The two only
        // disagree on consecutive capitals, which is why nothing failed until this was unified by accident.
        var registry = new PerCharacterSuffixRegistry();

        registry.RegisterTagType<EUTaxId, string>(x => x.Value).TableSuffix.ShouldBe("e_u_tax_id");
        registry.RegisterTagType<SSNumber>().TableSuffix.ShouldBe("s_s_number");
    }

    [Fact]
    public void the_first_registration_wins()
    {
        var first = theRegistry.RegisterTagType<EnrolleeId>("first");

        theRegistry.RegisterTagType<EnrolleeId>("second").ShouldBeSameAs(first);
        theRegistry.RegisterTagType<EnrolleeId>().ShouldBeSameAs(first);
        theRegistry.RegisterTagType<EnrolleeId, Guid>(x => x.Value, "third").ShouldBeSameAs(first);

        theRegistry.TagTypes.Count.ShouldBe(1);
    }

    [Fact]
    public void a_conflicting_inner_type_is_refused()
    {
        // Polecat and Fisher kept the first and quietly ignored this call's unwrapper
        theRegistry.RegisterTagType<EnrolleeId>();

        var ex = Should.Throw<InvalidOperationException>(() =>
            theRegistry.RegisterTagType<EnrolleeId, string>(x => x.Value.ToString()));

        ex.Message.ShouldContain("already registered");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void an_empty_suffix_is_refused_on_every_overload(string tableSuffix)
    {
        // Polecat and Fisher accepted this on the inferring overload, naming pc_event_tag_ or the like: a
        // real table every tag type registered that way would share
        Should.Throw<ArgumentException>(() => theRegistry.RegisterTagType<EnrolleeId>(tableSuffix));
        Should.Throw<ArgumentException>(() => theRegistry.RegisterTagType<EnrolleeId, Guid>(x => x.Value, tableSuffix));

        theRegistry.TagTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_null_suffix_asks_for_the_default()
    {
        theRegistry.RegisterTagType<EnrolleeId>(null!).TableSuffix.ShouldBe("enrollee_id");
    }

    [Fact]
    public void a_null_unwrapper_is_refused()
    {
        Should.Throw<ArgumentNullException>(() => theRegistry.RegisterTagType<EnrolleeId, Guid>(null!));
    }

    [Fact]
    public void the_unwrapper_is_what_reads_the_tag()
    {
        var registration = theRegistry.RegisterTagType<EnrolleeId, Guid>(x => x.Value);
        var id = Guid.NewGuid();

        registration.ExtractValue(new EnrolleeId(id)).ShouldBe(id);
    }

    [Fact]
    public void a_store_built_registration_can_be_added_and_the_first_still_wins()
    {
        // Marten's auto-discovery from projection identities
        var registry = new PerCharacterSuffixRegistry();

        var discovered = registry.Add(TagTypeRegistration.FromUnwrapper<EnrolleeId, Guid>(x => x.Value));
        var again = registry.Add(TagTypeRegistration.FromUnwrapper<EnrolleeId, Guid>(x => x.Value, "other"));

        again.ShouldBeSameAs(discovered);
        registry.TagTypes.Count.ShouldBe(1);
    }

    [Fact]
    public void an_unreadable_tag_is_explained_rather_than_thrown_raw()
    {
        // Polecat and Fisher surfaced the raw InvalidValueTypeException
        var ex = Should.Throw<InvalidOperationException>(() => theRegistry.RegisterTagType<TwoPropertyTagId>("two"));

        ex.Message.ShouldContain("opts.Events.RegisterTagType<TwoPropertyTagId, TInner>(x => x.Value, \"two\");");
        ex.InnerException.ShouldBeOfType<InvalidValueTypeException>();
    }

    [Fact]
    public void a_generic_that_cannot_be_closed_natively_names_the_explicit_overload()
    {
        var failure = new NotSupportedException("missing native code or metadata");

        var ex = Should.Throw<InvalidOperationException>(() =>
            EventRegistry.CreateTagType(typeof(EnrolleeId), "enrollee", _ => throw failure, canEmit: false));

        ex.Message.ShouldContain("opts.Events.RegisterTagType<EnrolleeId, Guid>(x => x.Value, \"enrollee\");");
        ex.Message.ShouldContain("Native AOT");
        ex.InnerException.ShouldBeSameAs(failure);
    }

    [Fact]
    public void a_missing_constructor_natively_names_the_explicit_overload_too()
    {
        var failure = new MissingMethodException("no parameterless constructor");

        Should.Throw<InvalidOperationException>(() =>
                EventRegistry.CreateTagType(typeof(EnrolleeId), "enrollee", _ => throw failure, canEmit: false))
            .InnerException.ShouldBeSameAs(failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_unreadable_tag_names_the_explicit_overload_under_a_jit_as_well(bool canEmit)
    {
        // InvalidValueTypeException is ambiguous: the tag may not be a single-value wrapper, or a trimmed
        // image may have lost the members that say so, and no runtime flag separates the two
        var failure = new InvalidValueTypeException(typeof(EnrolleeId), "Must be only a single public, 'gettable' property");

        var ex = Should.Throw<InvalidOperationException>(() =>
            EventRegistry.CreateTagType(typeof(EnrolleeId), "enrollee", _ => throw failure, canEmit));

        ex.Message.ShouldContain("opts.Events.RegisterTagType<EnrolleeId, Guid>(x => x.Value, \"enrollee\");");
        ex.Message.ShouldContain("trimmed");
        ex.InnerException.ShouldBeSameAs(failure);
    }

    [Fact]
    public void an_unrelated_failure_is_not_reinterpreted()
    {
        var failure = new ArgumentException("something else");

        Should.Throw<ArgumentException>(() =>
                EventRegistry.CreateTagType(typeof(EnrolleeId), null, _ => throw failure, canEmit: false))
            .ShouldBeSameAs(failure);
    }

    [Fact]
    public void a_closing_failure_under_a_jit_is_not_reinterpreted()
    {
        // Unlike the unreadable tag, this one is unambiguous: a JIT closed the generic, so a
        // NotSupportedException from inside the registration is about something else entirely
        var failure = new NotSupportedException("something else");

        Should.Throw<NotSupportedException>(() =>
                EventRegistry.CreateTagType(typeof(EnrolleeId), null, _ => throw failure, canEmit: true))
            .ShouldBeSameAs(failure);
    }

    [Fact]
    public void the_registration_line_reads_the_tag()
    {
        TagTypeRegistration.RegistrationLine(typeof(EnrolleeId), "enrollee", null)
            .ShouldBe("opts.Events.RegisterTagType<EnrolleeId, Guid>(x => x.Value, \"enrollee\");");
    }

    [Fact]
    public void the_registration_line_uses_placeholders_when_the_tag_cannot_be_read()
    {
        TagTypeRegistration.RegistrationLine(typeof(TwoPropertyTagId), "two", null)
            .ShouldBe("opts.Events.RegisterTagType<TwoPropertyTagId, TInner>(x => x.Value, \"two\");");
    }

    [Fact]
    public void the_registration_line_fills_in_the_default_suffix_and_the_aggregate()
    {
        TagTypeRegistration.RegistrationLine(typeof(EnrolleeId), null, typeof(Enrollment))
            .ShouldBe("opts.Events.RegisterTagType<EnrolleeId, Guid>(x => x.Value, \"enrollee_id\").ForAggregate<Enrollment>();");
    }

    private class PerCharacterSuffixRegistry : EventRegistry
    {
        protected override string DefaultTagTableSuffixFor(Type tagType) => tagType.Name switch
        {
            nameof(EUTaxId) => "e_u_tax_id",
            nameof(SSNumber) => "s_s_number",
            var name => name
        };

        public ITagTypeRegistration Add(ITagTypeRegistration registration) => AddTagType(registration);
    }
}

public record struct EnrolleeId(Guid Value);

public record struct EUTaxId(string Value);

public record struct SSNumber(string Value);

public record TwoPropertyTagId(Guid Value, string Label);

public class Enrollment;
