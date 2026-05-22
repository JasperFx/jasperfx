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
    [InlineData("one.")]
    [InlineData(".one")]
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

    [Fact]
    public void round_trips_dotted_server_name()
    {
        var id = new DatabaseId(
            "database-feature2.zorgdeclaraties-test.aws.topicus.healthcare",
            "feature2_claims2");

        var roundTripped = DatabaseId.Parse(id.ToString());

        roundTripped.ShouldBe(id);
    }

    [Fact]
    public void parse_legacy_dotted_server_name()
    {
        var id = DatabaseId.Parse(
            "database-feature2.zorgdeclaraties-test.aws.topicus.healthcare.feature2_claims2");

        id.Server.ShouldBe("database-feature2.zorgdeclaraties-test.aws.topicus.healthcare");
        id.Name.ShouldBe("feature2_claims2");
    }

    [Fact]
    public void escapes_dots_inside_segments()
    {
        var id = new DatabaseId("server.with.dots", "name.with.dots");

        id.ToString().ShouldBe("server%2Ewith%2Edots.name%2Ewith%2Edots");
        DatabaseId.Parse(id.ToString()).ShouldBe(id);
    }

    [Fact]
    public void round_trips_percent_encoded_text()
    {
        var id = new DatabaseId("server%2Ename", "db%25name");

        DatabaseId.Parse(id.ToString()).ShouldBe(id);
    }
}
