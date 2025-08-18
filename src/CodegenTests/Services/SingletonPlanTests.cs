using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace CodegenTests.Services;

public class SingletonPlanTests
{
    private readonly SingletonPlan theSingletonPlan =
        new(new ServiceDescriptor(typeof(IWidget), typeof(AWidget), ServiceLifetime.Singleton));

    private readonly IMethodVariables theVariables = Substitute.For<IMethodVariables>();

    [Fact]
    public void requires_service_provider_is_false()
    {
        theSingletonPlan.RequiresServiceProvider(theVariables).ShouldBeFalse();
    }

    [Fact]
    public void create_variable_builds_injected_singleton()
    {
        var variable =
            theSingletonPlan.CreateVariable(new ServiceVariables(theVariables, new List<InjectedSingleton>()));

        variable.ShouldBeOfType<InjectedSingleton>()
            .Descriptor.ShouldBe(theSingletonPlan.Descriptor);
    }
}