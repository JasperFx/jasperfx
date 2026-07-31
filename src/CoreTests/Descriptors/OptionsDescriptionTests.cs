using System.Text.Json;
using JasperFx.Descriptors;
using Shouldly;

namespace CoreTests.Descriptors;

public class OptionsDescriptionTests
{
    [Fact]
    public void serializable()
    {
        var description = new OptionsDescription();
        description.AddValue("Foo", 1);
        var children = new OptionSet() { Subject = ""};
        description.Sets["Children"] = children;
        children.SummaryColumns = ["a", "b"];
        children.Rows.Add(new OptionsDescription());

        description.ShouldBeSerializable();
    }

    [Fact]
    public void pick_up_tags_from_itagged()
    {
        var description = new OptionsDescription(new SomeObject());
        description.Tags.ShouldContain("blue");
        description.Tags.ShouldContain("green");
    }

    [Fact]
    public void children_and_sets_round_trip_through_default_json_options()
    {
        // Regression for #203 — `Children` and `Sets` were public fields, not
        // properties. System.Text.Json with its default options walks properties
        // only, so every [ChildDescription] and every AddChildSet(...) call was
        // being silently dropped at the JSON boundary (e.g. Marten's
        // EventGraph.MetadataConfig was invisible in CritterWatch).
        var description = new OptionsDescription { Subject = "Parent" };

        var child = new OptionsDescription { Subject = "Child" };
        child.AddValue("Foo", 42);
        description.Children["Inner"] = child;

        var set = description.AddChildSet("Members");
        set.Rows.Add(new OptionsDescription { Subject = "Row1" });
        set.Rows.Add(new OptionsDescription { Subject = "Row2" });

        // Default options — no IncludeFields = true override. This is what
        // both Wolverine.SignalR (JsonSerializerOptions.Web) and TS-side
        // codegen in CritterWatch effectively use.
        var json = JsonSerializer.Serialize(description);
        var round = JsonSerializer.Deserialize<OptionsDescription>(json);

        round.ShouldNotBeNull();
        round.Children.ShouldContainKey("Inner");
        round.Children["Inner"].Subject.ShouldBe("Child");
        round.Sets.ShouldContainKey("Members");
        round.Sets["Members"].Rows.Count.ShouldBe(2);
        round.Sets["Members"].Rows[0].Subject.ShouldBe("Row1");
        round.Sets["Members"].Rows[1].Subject.ShouldBe("Row2");
    }
}

public class describing_awkward_properties
{
    // Regression for the report in JasperFx/wolverine#3740: AzureServiceBusTransport.HostName threw a
    // NullReferenceException for credential-based connections, and because an OptionsDescription reads every
    // public property reflectively, that single getter took out the whole Wolverine ServiceCapabilities
    // snapshot -- so the monitoring console got nothing at all for the service, permanently.
    [Fact]
    public void a_throwing_getter_does_not_lose_the_whole_description()
    {
        var description = new OptionsDescription(new AwkwardObject());

        description.PropertyFor(nameof(AwkwardObject.Name))!.Value.ShouldBe("Rogue");
        description.PropertyFor(nameof(AwkwardObject.Tolerable))!.Value.ShouldBe("42");

        var explosive = description.PropertyFor(nameof(AwkwardObject.Explosive));
        explosive.ShouldNotBeNull();
        explosive.Value.ShouldStartWith(OptionsValue.UnreadablePrefix);
        explosive.Value.ShouldContain(nameof(NullReferenceException));
        explosive.RawValue.ShouldBeNull();
    }

    [Fact]
    public void the_exception_message_is_never_included()
    {
        // Descriptions are shipped to monitoring tools and go to some lengths to keep secrets out; exception
        // messages love to quote the offending configuration value
        var description = new OptionsDescription(new AwkwardObject());
        description.PropertyFor(nameof(AwkwardObject.Explosive))!.Value
            .ShouldNotContain(AwkwardObject.SecretishMessage);
    }

    [Fact]
    public void skips_indexers_and_set_only_properties()
    {
        var description = new OptionsDescription(new AwkwardObject());

        // "Item" is what an indexer is called in reflection -- reading one with no arguments throws
        description.Properties.ShouldNotContain(x => x.Name == "Item");
        description.Properties.ShouldNotContain(x => x.Name == nameof(AwkwardObject.WriteOnly));
    }

    [Fact]
    public void a_throwing_child_description_does_not_lose_the_whole_description()
    {
        var description = new OptionsDescription(new AwkwardParent());

        description.PropertyFor(nameof(AwkwardParent.Name))!.Value.ShouldBe("Storm");
        description.Children.ShouldNotContainKey(nameof(AwkwardParent.Child));
        description.PropertyFor(nameof(AwkwardParent.Child))!.Value
            .ShouldStartWith(OptionsValue.UnreadablePrefix);
    }

    [Fact]
    public void still_serializable_with_unreadable_properties()
    {
        new OptionsDescription(new AwkwardObject()).ShouldBeSerializable();
    }
}

public class AwkwardObject
{
    public const string SecretishMessage = "Endpoint=sb://myns.servicebus.windows.net;SharedAccessKey=shhh";

    public string Name { get; set; } = "Rogue";
    public int Tolerable => 42;

    public string Explosive => throw new NullReferenceException(SecretishMessage);

    public string WriteOnly { set { } }

    public string this[int index] => throw new NotSupportedException();
}

public class AwkwardParent
{
    public string Name { get; set; } = "Storm";

    [ChildDescription]
    public AwkwardObject Child => throw new InvalidOperationException("nope");
}

public class SomeObject : ITagged
{
    public string[] Tags => ["blue", "green"];
    public string Name { get; set; } = "Gambit";
}