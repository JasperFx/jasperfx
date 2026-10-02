using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.Aggregation;
using Shouldly;

namespace EventTests.Projections;

// #940: the source generator emits `file`-scoped evolver classes that the runtime constructs reflectively
// from GeneratedEvolverAttribute.EvolverType. Applications cannot root a file-scoped type themselves, so these
// annotations are the only thing keeping the evolver constructors alive under Native AOT / trimming.
public class GeneratedEvolverAttributeTests
{
    [Fact]
    public void evolver_type_property_preserves_public_constructors()
    {
        typeof(GeneratedEvolverAttribute)
            .GetProperty(nameof(GeneratedEvolverAttribute.EvolverType))!
            .GetCustomAttribute<DynamicallyAccessedMembersAttribute>()
            .ShouldNotBeNull()
            .MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.PublicConstructors).ShouldBeTrue();
    }

    [Fact]
    public void every_constructor_preserves_public_constructors_of_the_evolver_type()
    {
        var constructors = typeof(GeneratedEvolverAttribute).GetConstructors();
        constructors.Length.ShouldBeGreaterThan(0);

        foreach (var constructor in constructors)
        {
            var evolverType = constructor.GetParameters().Single(p => p.Name == "evolverType");
            evolverType.GetCustomAttribute<DynamicallyAccessedMembersAttribute>()
                .ShouldNotBeNull()
                .MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.PublicConstructors).ShouldBeTrue();
        }
    }
}
