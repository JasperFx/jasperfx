using JasperFx;
using Shouldly;

namespace CoreTests;

public class DocumentAlreadyExistsExceptionTests
{
    [Fact]
    public void no_inner_ctor_carries_document_type_and_id()
    {
        var ex = new DocumentAlreadyExistsException(typeof(Target), "abc");

        ex.DocumentType.ShouldBe(typeof(Target));
        ex.Id.ShouldBe("abc");
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public void inner_exception_ctor_carries_inner_plus_document_type_and_id()
    {
        var inner = new Exception("boom");
        var ex = new DocumentAlreadyExistsException(inner, typeof(Target), 42);

        ex.InnerException.ShouldBeSameAs(inner);
        ex.DocumentType.ShouldBe(typeof(Target));
        ex.Id.ShouldBe(42);
    }

    [Fact]
    public void inner_exception_ctor_tolerates_a_null_inner()
    {
        // Marten's closed-shape insert path constructs with a null inner.
        var ex = new DocumentAlreadyExistsException(null, typeof(Target), 42);

        ex.InnerException.ShouldBeNull();
        ex.DocumentType.ShouldBe(typeof(Target));
    }

    [Fact]
    public void message_uses_martens_fullname_format()
    {
        var ex = new DocumentAlreadyExistsException(typeof(Target), "abc");

        ex.Message.ShouldBe($"Document already exists {typeof(Target).FullName}: abc");
    }

    [Fact]
    public void doctype_alias_mirrors_document_type_for_marten_source_compat()
    {
        var ex = new DocumentAlreadyExistsException(typeof(Target), "abc");

        ex.DocType.ShouldBe(ex.DocumentType);
    }

    public class Target
    {
        public Guid Id { get; set; }
    }
}
