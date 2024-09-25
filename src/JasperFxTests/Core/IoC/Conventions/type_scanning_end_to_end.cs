using JasperFx.Core.IoC;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Widgets1;

namespace JasperFxTests.Core.IoC.Conventions;

public class type_scanning_end_to_end
{
    [Fact]
    public void can_configure_plugin_families_via_dsl()
    {
        var registry = new ServiceCollection();
        registry.AddSingleton<IRanger, RedRanger>();
        registry.Scan(x =>
        {
            x.TheCallingAssembly();
            x.ConnectImplementationsToTypesClosing(typeof(IFinder<>));
        });

        var container = registry.BuildServiceProvider();
        
        var firstStringFinder = container.GetRequiredService<IFinder<string>>().ShouldBeOfType<StringFinder>();
        var secondStringFinder = container.GetRequiredService<IFinder<string>>().ShouldBeOfType<StringFinder>();

        var firstIntFinder = container.GetRequiredService<IFinder<int>>().ShouldBeOfType<IntFinder>();
        var secondIntFinder = container.GetRequiredService<IFinder<int>>().ShouldBeOfType<IntFinder>();
    }

    [Fact]
    public void can_find_the_closed_finders()
    {
        var x = new ServiceCollection();
        x.AddSingleton<IRanger, RedRanger>();
        x.Scan(o =>
        {
            o.TheCallingAssembly();
            o.ConnectImplementationsToTypesClosing(typeof(IFinder<>));
        });

        var container = x.BuildServiceProvider();

        container.GetRequiredService<IFinder<string>>().ShouldBeOfType<StringFinder>();
        container.GetRequiredService<IFinder<int>>().ShouldBeOfType<IntFinder>();
        container.GetRequiredService<IFinder<double>>().ShouldBeOfType<DoubleFinder>();
    }

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void connect_implementations_with_lifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.Scan(o =>
        {
            o.TheCallingAssembly();
            o.ConnectImplementationsToTypesClosing(typeof(IFinder<>), lifetime);
        });

        services.TryFindDefault<IFinder<string>>().Lifetime.ShouldBe(lifetime);
        services.TryFindDefault<IFinder<int>>().Lifetime.ShouldBe(lifetime);
        services.TryFindDefault<IFinder<double>>().Lifetime.ShouldBe(lifetime);
    }

    [Fact]
    public void fails_on_closed_type()
    {
        Exception<InvalidOperationException>.ShouldBeThrownBy(
            () => { new GenericConnectionScanner(typeof(double)); });
    }

    [Fact]
    public void find_all_implementations()
    {
        var services = new ServiceCollection();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IShoes>();
            x.AssemblyContainingType<IWidget>();
            x.AddAllTypesOf<IWidget>();
        });
        
        var widgetTypes = services.Where(x => x.ServiceType == typeof(IWidget))
            .Select(x => x.ImplementationType).ToArray();

        widgetTypes.ShouldContain(typeof(MoneyWidget));
        widgetTypes.ShouldContain(typeof(AWidget));
    }

    [Fact]
    public void single_class_can_close_multiple_open_interfaces()
    {
        var x = new ServiceCollection();
        x.Scan(o =>
        {
            o.TheCallingAssembly();
            o.ConnectImplementationsToTypesClosing(typeof(IFinder<>));
            o.ConnectImplementationsToTypesClosing(typeof(IFindHandler<>));
        });

        var container = x.BuildServiceProvider();
        container.GetRequiredService<IFinder<decimal>>().ShouldBeOfType<SrpViolation>();
        container.GetRequiredService<IFindHandler<DateTime>>().ShouldBeOfType<SrpViolation>();
    }

    [Fact]
    public void single_class_can_close_the_same_open_interface_multiple_times()
    {
        var x = new ServiceCollection();
        x.Scan(o =>
        {
            o.TheCallingAssembly();
            o.ConnectImplementationsToTypesClosing(typeof(IFinder<>));
        });
        var container = x.BuildServiceProvider();
        container.GetRequiredService<IFinder<byte>>().ShouldBeOfType<SuperFinder>();
        container.GetRequiredService<IFinder<char>>().ShouldBeOfType<SuperFinder>();
        container.GetRequiredService<IFinder<uint>>().ShouldBeOfType<SuperFinder>();
    }

    [Fact]
    public void single_implementation()
    {
        var services = new ServiceCollection();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IShoes>();
            x.SingleImplementationsOfInterface();
        });


        var container = services.BuildServiceProvider();

        container.GetRequiredService<Muppet>().ShouldBeOfType<Grover>();

    }

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void single_implementation_with_lifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IShoes>();
            x.SingleImplementationsOfInterface(lifetime);
        });

        services.TryFindDefault<Muppet>().Lifetime.ShouldBe(lifetime);
    }

    [Fact]
    public void use_default_scanning()
    {
        var services = new ServiceCollection();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IShoes>();
            x.WithDefaultConventions();
        });
        
        services.TryFindDefault<IShoes>().ImplementationType.ShouldBe(typeof(Shoes));
        services.TryFindDefault<IShorts>().ImplementationType.ShouldBe(typeof(Shorts));
    }

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void use_default_scanning_with_lifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IShoes>();
            x.WithDefaultConventions(lifetime);
        });

        services.TryFindDefault<IShoes>().Lifetime.ShouldBe(lifetime);
        services.TryFindDefault<IShorts>().Lifetime.ShouldBe(lifetime);
    }

    [Fact]
    public void use_default_scanning_with_no_overrides()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRanger, RedRanger>();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IRanger>();
            x.WithDefaultConventions(OverwriteBehavior.Never);
        });

        var container = services.BuildServiceProvider();
        
        services.Single(x => x.ServiceType == typeof(IRanger))
            .ImplementationType.ShouldBe(typeof(RedRanger));
    }

    [Fact]
    public void use_default_scanning_with_if_newer_and_not_a_match()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRanger, RedRanger>();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IRanger>();
            x.WithDefaultConventions(OverwriteBehavior.NewType);
        });

        var container = services.BuildServiceProvider();

        container.GetServices<IRanger>().Select(x => x.GetType())
            .ShouldHaveTheSameElementsAs(typeof(RedRanger), typeof(Ranger));
    }

    [Fact]
    public void use_default_scanning_with_if_newer_and_a_match()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRanger, Ranger>();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IRanger>();
            x.WithDefaultConventions(OverwriteBehavior.NewType);
        });

        var container = services.BuildServiceProvider();
        container.GetServices<IRanger>().Single().ShouldBeOfType<Ranger>();
    }

    [Fact]
    public void connect_implementations_with_lifetime_rule()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRanger, Ranger>();
        services.Scan(x =>
        {
            x.AssemblyContainingType<IRanger>();
            x.ConnectImplementationsToTypesClosing(typeof(IFinder<>), type => type.HasConstructorsWithArguments()
                ? ServiceLifetime.Scoped
                : ServiceLifetime.Singleton);
        });

        var container = services.BuildServiceProvider();

        var scope1 = container.CreateScope();

        var scope2 = container.CreateScope();
        
        scope1.ServiceProvider.GetServices<IFinder<int>>().ShouldBeSameAs(scope2.ServiceProvider.GetServices<IFinder<int>>());
        scope1.ServiceProvider.GetServices<IFinder<string>>().ShouldNotBeSameAs(scope2.ServiceProvider.GetServices<IFinder<string>>());
    }

    public interface IFinder<T>
    {
    }

    public class StringFinder : IFinder<string>
    {
        public StringFinder(IRanger ranger)
        {
        }
    }

    public class IntFinder : IFinder<int>
    {
    }

    public class DoubleFinder : IFinder<double>
    {
    }

    public interface IFindHandler<T>
    {
    }

    public class SrpViolation : IFinder<decimal>, IFindHandler<DateTime>
    {
    }

    public class SuperFinder : IFinder<byte>, IFinder<char>, IFinder<uint>
    {
    }
}

public interface IRanger
{
}

public class Ranger : IRanger
{
}

public class RedRanger : IRanger
{
}

public interface Muppet
{
}

public class Grover : Muppet
{
}

public interface IShoes
{
}

public class Shoes : IShoes
{
}

public interface IShorts
{
}

public class Shorts : IShorts
{
}

public class SingleImplementationScannerTester
{
    private readonly ServiceProvider container;

    public SingleImplementationScannerTester()
    {
        var services = new ServiceCollection();
        
        services.Scan(x =>
        {
            x.TheCallingAssembly();
            x.IncludeNamespaceContainingType<SingleImplementationScannerTester>();
            x.SingleImplementationsOfInterface();
        });

        container = services.BuildServiceProvider();
    }

    [Fact]
    public void registers_plugins_that_only_have_a_single_implementation()
    {
        container.GetRequiredService<IOnlyHaveASingleConcreteImplementation>()
            .ShouldBeOfType<MyNameIsNotConventionallyRelatedToMyInterface>();
    }

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void single_implementation_with_lifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.Scan(x =>
        {
            x.TheCallingAssembly();
            x.IncludeNamespaceContainingType<SingleImplementationScannerTester>();
            x.SingleImplementationsOfInterface(lifetime);
        });
        
        
        services.TryFindDefault<IOnlyHaveASingleConcreteImplementation>().Lifetime.ShouldBe(lifetime);
    }
}

public interface IOnlyHaveASingleConcreteImplementation
{
}

public class MyNameIsNotConventionallyRelatedToMyInterface : IOnlyHaveASingleConcreteImplementation
{
}

public interface IHaveMultipleConcreteImplementations
{
}

public class FirstConcreteImplementation : IHaveMultipleConcreteImplementations
{
}

public class SecondConcreteImplementation : IHaveMultipleConcreteImplementations
{
}

public class TypeFindingTester
{
    private readonly IServiceProvider container;

    public TypeFindingTester()
    {
        var registry = new ServiceCollection();
        registry.Scan(x =>
        {
            x.TheCallingAssembly();
            x.AddAllTypesOf<TypeIWantToFind>();
            x.AddAllTypesOf<OtherType>();
        });

        container = registry.BuildServiceProvider();
    }

    [Fact]
    public void FoundTheRightNumberOfInstancesForATypeWithNoPlugins()
    {
        container.GetServices<TypeIWantToFind>().Count()
            .ShouldBe(3);
    }

    [Fact]
    public void FoundTheRightNumberOfInstancesForATypeWithNoPlugins2()
    {
        container.GetServices<OtherType>().Count().ShouldBe(2);
    }

    [Theory]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void add_all_types_with_lifetime(ServiceLifetime lifetime)
    {
        var registry = new ServiceCollection();
        registry.Scan(x =>
        {
            x.TheCallingAssembly();
            x.AddAllTypesOf<TypeIWantToFind>(lifetime);
            x.AddAllTypesOf<OtherType>(lifetime);
        });

        registry.TryFindDefault<TypeIWantToFind>().Lifetime.ShouldBe(lifetime);
        registry.TryFindDefault<OtherType>().Lifetime.ShouldBe(lifetime);
    }

    public interface IOpenGeneric<T>
    {
        void Nop();
    }

    public interface IAnotherOpenGeneric<T>
    {
    }

    public class ConcreteOpenGeneric<T> : IOpenGeneric<T>
    {
        public void Nop()
        {
        }
    }

    public class StringOpenGeneric : ConcreteOpenGeneric<string>
    {
    }

}

public interface TypeIWantToFind
{
}

public class RedType
{
}

public class BlueType : TypeIWantToFind
{
}

public class PurpleType : TypeIWantToFind
{
}

public class YellowType : TypeIWantToFind
{
}

public class GreenType : TypeIWantToFind
{
    private GreenType()
    {
    }
}

public abstract class OrangeType : TypeIWantToFind
{
}

public class OtherType
{
}

public class DifferentOtherType : OtherType
{
}

public interface INormalType
{
}

//[Pluggable("First")]
public class NormalTypeWithPluggableAttribute : INormalType
{
}

public class SecondNormalType : INormalType
{
}