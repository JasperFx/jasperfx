using JasperFx.Events.Tags;
using Shouldly;

namespace EventTests.Tags;

public class TagTypeRegistrationTests
{
    [Fact]
    public void create_registration_for_string_based_id()
    {
        var reg = new TagTypeRegistration(typeof(StudentId));

        reg.TagType.ShouldBe(typeof(StudentId));
        reg.SimpleType.ShouldBe(typeof(string));
        reg.TableSuffix.ShouldBe("student_id");
    }

    [Fact]
    public void create_registration_for_guid_based_id()
    {
        var reg = new TagTypeRegistration(typeof(InvoiceId));

        reg.TagType.ShouldBe(typeof(InvoiceId));
        reg.SimpleType.ShouldBe(typeof(Guid));
        reg.TableSuffix.ShouldBe("invoice_id");
    }

    [Fact]
    public void create_registration_with_custom_table_suffix()
    {
        var reg = new TagTypeRegistration(typeof(StudentId), "custom_student");

        reg.TableSuffix.ShouldBe("custom_student");
    }

    [Fact]
    public void extract_value_from_string_id()
    {
        var reg = new TagTypeRegistration(typeof(StudentId));
        var value = reg.ExtractValue(new StudentId("test-value"));

        value.ShouldBe("test-value");
    }

    [Fact]
    public void extract_value_from_guid_id()
    {
        var reg = new TagTypeRegistration(typeof(InvoiceId));
        var guid = Guid.NewGuid();
        var value = reg.ExtractValue(new InvoiceId(guid));

        value.ShouldBe(guid);
    }

    [Fact]
    public void for_aggregate_generic()
    {
        var reg = new TagTypeRegistration(typeof(StudentId));
        reg.AggregateType.ShouldBeNull();

        reg.ForAggregate<Student>();
        reg.AggregateType.ShouldBe(typeof(Student));
    }

    [Fact]
    public void for_aggregate_type()
    {
        var reg = new TagTypeRegistration(typeof(StudentId));
        reg.ForAggregate(typeof(Student));
        reg.AggregateType.ShouldBe(typeof(Student));
    }

    [Fact]
    public void for_aggregate_returns_self_for_fluent_chaining()
    {
        var reg = new TagTypeRegistration(typeof(StudentId));
        var result = reg.ForAggregate<Student>();
        result.ShouldBeSameAs(reg);
    }
}

// Test aggregate type
public class Student
{
    public StudentId Id { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
}
