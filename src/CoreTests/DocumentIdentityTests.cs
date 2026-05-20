using JasperFx;
using Shouldly;

namespace CoreTests;

public class DocumentIdentityTests
{
    [Fact]
    public void valid_id_types_are_the_canonical_four()
    {
        DocumentIdentity.ValidIdTypes.ShouldBe([typeof(int), typeof(Guid), typeof(long), typeof(string)]);
    }

    [Fact]
    public void resolves_conventional_id_property()
    {
        DocumentIdentity.FindIdMember(typeof(ConventionalId))!.Name.ShouldBe("Id");
    }

    [Fact]
    public void resolves_id_case_insensitively()
    {
        DocumentIdentity.FindIdMember(typeof(LowercaseId))!.Name.ShouldBe("id");
    }

    [Fact]
    public void identity_attribute_takes_priority_over_conventional_id()
    {
        DocumentIdentity.FindIdMember(typeof(AttributedNonId))!.Name.ShouldBe("Code");
    }

    [Fact]
    public void supports_fields_not_just_properties()
    {
        // Polecat was property-only; the lifted helper returns MemberInfo and finds fields too.
        DocumentIdentity.FindIdMember(typeof(FieldId))!.Name.ShouldBe("Id");
    }

    [Fact]
    public void ignores_id_of_a_non_valid_type()
    {
        // A DateTime "Id" is not one of the canonical valid id types.
        DocumentIdentity.FindIdMember(typeof(WrongTypeId)).ShouldBeNull();
    }

    [Fact]
    public void returns_null_when_no_identity_member()
    {
        DocumentIdentity.FindIdMember(typeof(NoId)).ShouldBeNull();
    }

    [Fact]
    public void predicate_overload_lets_a_store_widen_the_valid_types()
    {
        // The default predicate rejects a DateTime Id; a store-supplied predicate can accept it.
        // This is the seam stores use for strong-typed-id support without provider side effects here.
        DocumentIdentity.FindIdMember(typeof(WrongTypeId), t => t == typeof(DateTime))!.Name.ShouldBe("Id");
    }

    private sealed class ConventionalId
    {
        public Guid Id { get; set; }
    }

    private sealed class LowercaseId
    {
        public string id { get; set; } = "";
    }

    private sealed class AttributedNonId
    {
        [Identity] public string Code { get; set; } = "";
        public Guid Id { get; set; }
    }

    private sealed class FieldId
    {
#pragma warning disable CS0649
        public long Id;
#pragma warning restore CS0649
    }

    private sealed class WrongTypeId
    {
        public DateTime Id { get; set; }
    }

    private sealed class NoId
    {
        public string Name { get; set; } = "";
    }
}
