using JasperFx.Events.Descriptors;
using Shouldly;

namespace EventTests.Descriptors;

public class EventStoreUsageTests
{
    [Fact]
    public void get_the_version_from_the_subject()
    {
        var usage = new EventStoreUsage(new Uri("marten://main"), new MyThing());
        usage.Version.ShouldBe(GetType().Assembly.GetName().Version);
    }
}

public class MyThing
{
    
}