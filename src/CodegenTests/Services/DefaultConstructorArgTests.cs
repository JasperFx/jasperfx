using JasperFx;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodegenTests.Services;

public class DefaultConstructorArgTests
{
    [Fact]
    public void can_resolve_type_with_default_primitive_parameter()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWidget, AWidget>();
        var graph = new ServiceContainer(services, services.BuildServiceProvider());

        // TypeWithDefaultBool has a constructor: (IWidget widget, bool flag = false)
        // The bool parameter has a default value, so it should be resolvable
        var descriptor = new ServiceDescriptor(typeof(TypeWithDefaultBool), typeof(TypeWithDefaultBool), ServiceLifetime.Scoped);
        var result = ConstructorPlan.TryBuildPlan(new List<ServiceDescriptor>(), descriptor, graph, out var plan);

        result.ShouldBeTrue();
        plan.ShouldBeOfType<ConstructorPlan>();
    }

    [Fact]
    public void can_resolve_type_with_default_string_parameter()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWidget, AWidget>();
        var graph = new ServiceContainer(services, services.BuildServiceProvider());

        var descriptor = new ServiceDescriptor(typeof(TypeWithDefaultString), typeof(TypeWithDefaultString), ServiceLifetime.Scoped);
        var result = ConstructorPlan.TryBuildPlan(new List<ServiceDescriptor>(), descriptor, graph, out var plan);

        result.ShouldBeTrue();
        plan.ShouldBeOfType<ConstructorPlan>();
    }

    [Fact]
    public void can_resolve_type_with_default_int_parameter()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWidget, AWidget>();
        var graph = new ServiceContainer(services, services.BuildServiceProvider());

        var descriptor = new ServiceDescriptor(typeof(TypeWithDefaultInt), typeof(TypeWithDefaultInt), ServiceLifetime.Scoped);
        var result = ConstructorPlan.TryBuildPlan(new List<ServiceDescriptor>(), descriptor, graph, out var plan);

        result.ShouldBeTrue();
        plan.ShouldBeOfType<ConstructorPlan>();
    }

    [Fact]
    public void can_resolve_type_with_multiple_default_parameters()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWidget, AWidget>();
        var graph = new ServiceContainer(services, services.BuildServiceProvider());

        var descriptor = new ServiceDescriptor(typeof(TypeWithMultipleDefaults), typeof(TypeWithMultipleDefaults), ServiceLifetime.Scoped);
        var result = ConstructorPlan.TryBuildPlan(new List<ServiceDescriptor>(), descriptor, graph, out var plan);

        result.ShouldBeTrue();
        plan.ShouldBeOfType<ConstructorPlan>();
    }

    [Fact]
    public void still_rejects_unresolvable_primitive_without_default()
    {
        var services = new ServiceCollection();
        services.AddScoped<IWidget, AWidget>();
        var graph = new ServiceContainer(services, services.BuildServiceProvider());

        // TypeWithRequiredBool has: (IWidget widget, bool flag) — no default, should fail
        var descriptor = new ServiceDescriptor(typeof(TypeWithRequiredBool), typeof(TypeWithRequiredBool), ServiceLifetime.Scoped);
        ConstructorPlan.TryBuildPlan(new List<ServiceDescriptor>(), descriptor, graph, out var plan);

        // Should not produce a valid ConstructorPlan since bool can't be resolved from DI
        plan.ShouldNotBeOfType<ConstructorPlan>();
    }
}

// Test types
public class TypeWithDefaultBool
{
    public IWidget Widget { get; }
    public bool Flag { get; }

    public TypeWithDefaultBool(IWidget widget, bool flag = false)
    {
        Widget = widget;
        Flag = flag;
    }
}

public class TypeWithDefaultString
{
    public IWidget Widget { get; }
    public string Name { get; }

    public TypeWithDefaultString(IWidget widget, string name = "default")
    {
        Widget = widget;
        Name = name;
    }
}

public class TypeWithDefaultInt
{
    public IWidget Widget { get; }
    public int Count { get; }

    public TypeWithDefaultInt(IWidget widget, int count = 42)
    {
        Widget = widget;
        Count = count;
    }
}

public class TypeWithMultipleDefaults
{
    public IWidget Widget { get; }
    public bool Enabled { get; }
    public int Timeout { get; }
    public string Label { get; }

    public TypeWithMultipleDefaults(IWidget widget, bool enabled = true, int timeout = 30, string label = "test")
    {
        Widget = widget;
        Enabled = enabled;
        Timeout = timeout;
        Label = label;
    }
}

public class TypeWithRequiredBool
{
    public IWidget Widget { get; }
    public bool Flag { get; }

    public TypeWithRequiredBool(IWidget widget, bool flag)
    {
        Widget = widget;
        Flag = flag;
    }
}
