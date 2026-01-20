using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

public class DatabaseIdTests
{
    [Fact]
    public void parse()
    {
        var id = DatabaseId.Parse("foo.bar");
        id.Server.ShouldBe("foo");
        id.Name.ShouldBe("bar");
        
    }

    [Fact]
    public void identity()
    {
        var id = new DatabaseId("localhost", "tenant1");
        id.Identity.ShouldBe("localhost.tenant1");
    }

    [Fact]
    public void try_parse_happy_path()
    {
        DatabaseId.TryParse("localhost.db2", out var id).ShouldBeTrue();
        id.Server.ShouldBe("localhost");
        id.Name.ShouldBe("db2");
    }

    [Theory]
    [InlineData("one")]
    [InlineData("one,two")]
    [InlineData("one.two.three")]
    public void try_parse_sad_path(string text)
    {
        DatabaseId.TryParse(text, out var id).ShouldBeFalse();
    }

    [Fact]
    public void escape_slashes()
    {
        var id = new DatabaseId("/some/host", "tom");
        id.ToString().ShouldBe("~some~host.tom");
    }

    [Fact]
    public void parse_with_tilde()
    {
        var id = DatabaseId.Parse("~some~host.tom");
        id.Server.ShouldBe("/some/host");
        id.Name.ShouldBe("tom");
    }

    [Fact]
    public void try_parse_with_tilde()
    {
        DatabaseId.TryParse("~some~host.tom", out var id).ShouldBeTrue();
        id.Server.ShouldBe("/some/host");
        id.Name.ShouldBe("tom");
    }
}