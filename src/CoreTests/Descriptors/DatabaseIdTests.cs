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
    [InlineData(".one")]
    public void try_parse_sad_path(string text)
    {
        DatabaseId.TryParse(text, out var id).ShouldBeFalse();
    }

    [Fact]
    public void try_parse_empty_database_name()
    {
        // A connection string with no Database= yields a DatabaseId with an empty Name; its
        // serialized form ("localhost.") must round-trip rather than be rejected. See wolverine#3170.
        DatabaseId.TryParse("localhost.", out var id).ShouldBeTrue();
        id.Server.ShouldBe("localhost");
        id.Name.ShouldBe("");
    }

    [Fact]
    public void ctor_with_empty_name_round_trips_through_parse()
    {
        var id = new DatabaseId("localhost", "");

        DatabaseId.TryParse(id.Identity, out var fromIdentity).ShouldBeTrue();
        fromIdentity.ShouldBe(id);

        DatabaseId.Parse(id.ToString()).ShouldBe(id);
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

        id.ToString().ShouldBe("server!with!dots.name!with!dots");
        DatabaseId.Parse(id.ToString()).ShouldBe(id);
    }

    [Fact]
    public void round_trips_percent_encoded_text()
    {
        var id = new DatabaseId("server%2Ename", "db%25name");

        DatabaseId.Parse(id.ToString()).ShouldBe(id);
    }

    // GH-599: the escaped form's only consumer interpolates it into an agent URI and parses it back out of
    // uri.Segments, so the escaping is worthless unless it survives System.Uri canonicalisation. "%2E"
    // encodes '.', which is unreserved in RFC 3986, so Uri decodes it right back and the separator became
    // ambiguous again.

    [Theory]
    [InlineData("localhost", "tenant1")]
    [InlineData("server.with.dots", "name.with.dots")]
    [InlineData("database-feature2.zorgdeclaraties-test.aws.topicus.healthcare", "feature2_claims2")]
    [InlineData("/some/host", "tom")]
    [InlineData("server%2Ename", "db%25name")]
    [InlineData("host!with!bangs", "name!too")]
    [InlineData("host~with~tildes", "name~too")]
    [InlineData("localhost", "")]
    public void survives_a_system_uri_round_trip(string server, string name)
    {
        var id = new DatabaseId(server, name);

        var uri = new Uri($"marten://main/{id}/p/all/1");

        // Uri.ToString() decodes percent-escapes; Uri.OriginalString does not. Both must agree, or the
        // same identity reaches a client spelled two ways depending on how it travelled.
        uri.ToString().ShouldBe(uri.OriginalString);
        uri.AbsoluteUri.ShouldBe(uri.OriginalString);

        DatabaseId.Parse(uri.Segments[1].Trim('/')).ShouldBe(id);
    }

    [Fact]
    public void identity_and_to_string_are_the_same_spelling()
    {
        var id = new DatabaseId("server.with.dots", "name.with.dots");

        id.Identity.ShouldBe(id.ToString());
        DatabaseId.Parse(id.Identity).ShouldBe(id);
    }

    [Fact]
    public void round_trips_a_literal_bang()
    {
        // '!' is the dot escape now, so a literal one has to be escaped in turn.
        var id = new DatabaseId("host!name", "db!name");

        id.ToString().ShouldBe("host!!name.db!!name");
        DatabaseId.Parse(id.ToString()).ShouldBe(id);
    }

    [Fact]
    public void round_trips_a_literal_tilde()
    {
        // Pre-existing hole: '~' is the slash escape but a literal '~' was never escaped, so
        // new DatabaseId("a~b", "c") used to come back out as "a/b".
        var id = new DatabaseId("host~name", "db~name");

        DatabaseId.Parse(id.ToString()).ShouldBe(id);
    }

    [Fact]
    public void still_parses_the_legacy_percent_2e_spelling()
    {
        // Agent URIs written before GH-599 are persisted; they must keep parsing.
        var id = DatabaseId.Parse("server%2Ewith%2Edots.name%2Ewith%2Edots");

        id.Server.ShouldBe("server.with.dots");
        id.Name.ShouldBe("name.with.dots");
    }

    [Fact]
    public void still_parses_the_legacy_tilde_as_a_slash()
    {
        DatabaseId.Parse("~some~host.tom").ShouldBe(new DatabaseId("/some/host", "tom"));
    }
}
