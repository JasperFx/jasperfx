using System.Reflection;
using System.Reflection.Emit;
using EventTests.EventModeling;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;
using ApplyToVolunteer = EventTests.EventModeling.SpecificationLinkTests.ApplyToVolunteer;

[assembly: SpecificationManifest(typeof(GeneratedSpecificationManifest))]

namespace EventTests.EventModeling;

// What a spec generator writes: a static All over the assembly's specifications
internal static class GeneratedSpecificationManifest
{
    internal static IReadOnlyList<SpecificationBindingDescriptor> All { get; } =
    [
        new("apply to volunteer/volunteer application submitted")
        {
            CommandType = TypeDescriptor.For(typeof(ApplyToVolunteer))
        }
    ];
}

/// <summary>
/// jasperfx#997 — a model command reads a spec assembly's manifest without referencing the tool that wrote it.
/// </summary>
public class SpecificationManifestTests
{
    [Fact]
    public void reads_the_bindings_through_the_jasperfx_attribute()
    {
        SpecificationManifest.In(typeof(SpecificationManifestTests).Assembly)
            .ShouldHaveSingleItem().Identity.ShouldBe("apply to volunteer/volunteer application submitted");
    }

    [Fact]
    public void an_assembly_without_a_manifest_has_no_bindings()
    {
        SpecificationManifest.In(typeof(Shouldly.Should).Assembly).ShouldBeEmpty();
    }

    [Fact]
    public void reads_the_bindings_through_bobcats_older_attribute_by_name()
    {
        // An assembly carrying Bobcat.SpecificationManifestAttribute, as Bobcat versions before this one write it
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("OlderBobcatSpecs"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("OlderBobcatSpecs");

        var attribute = module.DefineType("Bobcat.SpecificationManifestAttribute",
            TypeAttributes.Public | TypeAttributes.Sealed, typeof(Attribute));
        var ctor = attribute.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(Type)]);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
        var attributeType = attribute.CreateType();

        assembly.SetCustomAttribute(new CustomAttributeBuilder(attributeType.GetConstructor([typeof(Type)])!,
            [typeof(GeneratedSpecificationManifest)]));

        SpecificationManifest.In(assembly)
            .ShouldHaveSingleItem().CommandType!.FullName.ShouldBe(typeof(ApplyToVolunteer).FullName);
    }

    [Fact]
    public void loads_a_spec_assembly_from_its_path()
    {
        SpecificationManifest.Load(typeof(SpecificationManifestTests).Assembly.Location)
            .ShouldHaveSingleItem().Identity.ShouldBe("apply to volunteer/volunteer application submitted");
    }

    [Fact]
    public void a_missing_spec_assembly_says_where_it_looked()
    {
        Should.Throw<FileNotFoundException>(() => SpecificationManifest.Load("nowhere/Specs.dll"))
            .Message.ShouldContain("Specs.dll");
    }

    [Fact]
    public void the_bindings_link_onto_the_model()
    {
        var model = new EventModelBuilder();
        model.Command<ApplyToVolunteer>();
        var descriptor = new EventModelDescriptor("Volunteers", model.BuildSlices());

        var linked = EventModelSpecifications.Link(descriptor,
            SpecificationManifest.In(typeof(SpecificationManifestTests).Assembly));

        linked.Slices.Single().Specifications.ShouldHaveSingleItem().Identity
            .ShouldBe("apply to volunteer/volunteer application submitted");
    }
}
