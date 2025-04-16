using JasperFx.Core;
using Shouldly;

namespace CoreTests.Core;

public class UriExtensionsTests
{
    [Theory]
    [InlineData("foo://one", "foo://", true)]
    [InlineData("foo://one", "foo://one", true)]
    [InlineData("foo://two", "foo://", true)]
    [InlineData("foo://two/longer", "foo://", true)]
    [InlineData("foo://two/longer/still", "foo://", true)]
    [InlineData("foo://two/longer/still", "foo://two", true)]
    [InlineData("foo://two/longer/still", "foo://two/*", true)]
    [InlineData("foo://two/longer/still", "foo://two/longer/*", true)]
    [InlineData("foo://three/longer/still", "foo://two/longer/*", false)]
    [InlineData("foo://three/longer/still", "foo://three/*/still", true)]
    [InlineData("foo://three/", "foo://three/*/still", false)]
    [InlineData("foo://three/longer/still", "foo://two/*/still", false)]
    [InlineData("foo://two/longer/still", "foo://two/*/still", true)]
    [InlineData("foo://three/longer/still", "foo://two/notlonger/*", false)]
    [InlineData("bar://two/longer/still", "foo://", false)]
    [InlineData("bar://two/longer/still", "foo://two/*", false)]
    [InlineData("bar://two/longer/still", "foo://two/longer/*", false)]
    public void matches(string target, string match, bool matches)
    {
        new Uri(target).Matches(new Uri(match)).ShouldBe(matches);
    }
}