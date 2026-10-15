using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

public class TagTypeRegistrationTests
{
    [Fact]
    public void create_registration_for_string_based_id()
    {
        var reg = TagTypeRegistration.Create<StudentId>();

        reg.TagType.ShouldBe(typeof(StudentId));
        reg.SimpleType.ShouldBe(typeof(string));
        reg.TableSuffix.ShouldBe("student_id");
    }

    [Fact]
    public void create_registration_for_guid_based_id()
    {
        var reg = TagTypeRegistration.Create<InvoiceId>();

        reg.TagType.ShouldBe(typeof(InvoiceId));
        reg.SimpleType.ShouldBe(typeof(Guid));
        reg.TableSuffix.ShouldBe("invoice_id");
    }

    [Fact]
    public void create_registration_with_custom_table_suffix()
    {
        var reg = TagTypeRegistration.Create<StudentId>("custom_student");

        reg.TableSuffix.ShouldBe("custom_student");
    }

    [Fact]
    public void extract_value_from_string_id()
    {
        var reg = TagTypeRegistration.Create<StudentId>();
        var value = reg.ExtractValue(new StudentId("test-value"));

        value.ShouldBe("test-value");
    }

    [Fact]
    public void extract_value_from_guid_id()
    {
        var reg = TagTypeRegistration.Create<InvoiceId>();
        var guid = Guid.NewGuid();
        var value = reg.ExtractValue(new InvoiceId(guid));

        value.ShouldBe(guid);
    }

    [Fact]
    public void for_aggregate_generic()
    {
        var reg = TagTypeRegistration.Create<StudentId>();
        reg.AggregateType.ShouldBeNull();

        reg.ForAggregate<Student>();
        reg.AggregateType.ShouldBe(typeof(Student));
    }

    [Fact]
    public void for_aggregate_type()
    {
        var reg = TagTypeRegistration.Create<StudentId>();
        reg.ForAggregate(typeof(Student));
        reg.AggregateType.ShouldBe(typeof(Student));
    }

    [Fact]
    public void for_aggregate_returns_self_for_fluent_chaining()
    {
        var reg = TagTypeRegistration.Create<StudentId>();
        var result = reg.ForAggregate<Student>();
        result.ShouldBeSameAs(reg);
    }

    [Fact]
    public void explicit_unwrapper_registration_matches_the_reflective_one()
    {
        var reflective = TagTypeRegistration.Create<InvoiceId>();
        var explicitly = TagTypeRegistration.FromUnwrapper<InvoiceId, Guid>(x => x.Value);

        explicitly.ShouldBeOfType<TagTypeRegistration<InvoiceId, Guid>>();
        explicitly.TagType.ShouldBe(reflective.TagType);
        explicitly.SimpleType.ShouldBe(reflective.SimpleType);
        explicitly.TableSuffix.ShouldBe(reflective.TableSuffix);
    }

    [Fact]
    public void explicit_unwrapper_registration_with_custom_table_suffix()
    {
        var reg = TagTypeRegistration.FromUnwrapper<StudentId, string>(x => x.Value, "custom_student");

        reg.TableSuffix.ShouldBe("custom_student");
    }

    [Fact]
    public void explicit_unwrapper_extracts_the_value()
    {
        var reg = TagTypeRegistration.FromUnwrapper<StudentId, string>(x => x.Value);

        reg.ExtractValue(new StudentId("test-value")).ShouldBe("test-value");
    }

    [Fact]
    public void explicit_unwrapper_does_not_need_a_value_type_shape()
    {
        // ValueTypeInfo.ForType refuses a type with more than one public property, so the
        // reflective factory cannot register this. The explicit unwrapper never inspects it.
        Should.Throw<InvalidValueTypeException>(() => TagTypeRegistration.Create<TwoPropertyId>());

        var reg = TagTypeRegistration.FromUnwrapper<TwoPropertyId, Guid>(x => x.Value);

        var guid = Guid.NewGuid();
        reg.ExtractValue(new TwoPropertyId(guid, "label")).ShouldBe(guid);
    }

    [Fact]
    public void create_stays_resolvable_by_name()
    {
        // Marten looks Create<TTag> up by name, in its event graph's type initializer. A second
        // method called Create would throw AmbiguousMatchException there, in every Marten version
        // that uses that lookup.
        typeof(TagTypeRegistration).GetMethod(nameof(TagTypeRegistration.Create)).ShouldNotBeNull();
    }

    [Fact]
    public void create_is_annotated_so_aot_builds_report_it()
    {
        // Annotated rather than suppressed, so a trimmed or Native AOT consumer is pointed at FromUnwrapper
        var create = typeof(TagTypeRegistration).GetMethod(nameof(TagTypeRegistration.Create))!;

        create.GetCustomAttribute<RequiresDynamicCodeAttribute>().ShouldNotBeNull()
            .Message.ShouldContain(nameof(TagTypeRegistration.FromUnwrapper));
        create.GetCustomAttribute<RequiresUnreferencedCodeAttribute>().ShouldNotBeNull()
            .Message.ShouldContain(nameof(TagTypeRegistration.FromUnwrapper));
    }

    [Fact]
    public void explicit_unwrapper_is_required()
    {
        Should.Throw<ArgumentNullException>(() => new TagTypeRegistration<StudentId, string>((Func<StudentId, string>)null!, "student"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void explicit_unwrapper_table_suffix_is_required(string? tableSuffix)
    {
        // An empty suffix would name the table mt_event_tag_; FromUnwrapper's own default never reaches this
        Should.Throw<ArgumentException>(() => new TagTypeRegistration<StudentId, string>(x => x.Value, tableSuffix!));
    }

    [Fact]
    public void explicit_unwrapper_registration_for_aggregate()
    {
        var reg = TagTypeRegistration.FromUnwrapper<StudentId, string>(x => x.Value);

        reg.ForAggregate<Student>().ShouldBeSameAs(reg);
        reg.AggregateType.ShouldBe(typeof(Student));
    }
}

public record TwoPropertyId(Guid Value, string Label);

// Test aggregate type
public class Student
{
    public StudentId Id { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
}
