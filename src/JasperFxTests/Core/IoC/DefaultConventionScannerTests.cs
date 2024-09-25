using JasperFx.Core.IoC;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Widgets1;

namespace JasperFxTests.Core.IoC;

public class DefaultConventionScannerTests
{
    [Fact]
    public void the_default_overwrite_behavior_is_if_new()
    {
        new DefaultConventionScanner()
            .Overwrites.ShouldBe(OverwriteBehavior.NewType);
    }

    [Fact]
    public void should_add_always()
    {
        var scanner = new DefaultConventionScanner
        {
            Overwrites = OverwriteBehavior.Always
        };

        var services = new ServiceCollection();

        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();

        services.AddTransient<IWidget, BWidget>();
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();

        services.AddTransient<IWidget>(x => new AWidget());
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();

        services.AddTransient<IWidget, AWidget>();
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();
    }

    [Fact]
    public void should_add_with_overwrite_never()
    {
        var scanner = new DefaultConventionScanner
        {
            Overwrites = OverwriteBehavior.Never
        };

        var services = new ServiceCollection();

        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();

        services.AddTransient<IWidget, BWidget>();
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeFalse();

        services = new ServiceCollection();
        services.AddTransient<IWidget>(x => new AWidget());
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeFalse();

        services = new ServiceCollection();
        services.AddTransient<IWidget, AWidget>();
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeFalse();
    }

    [Fact]
    public void should_add_with_overwrite_if_newer()
    {
        var scanner = new DefaultConventionScanner
        {
            Overwrites = OverwriteBehavior.NewType
        };

        var services = new ServiceCollection();

        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();

        services.AddTransient<IWidget, BWidget>();
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();

        services = new ServiceCollection();
        services.AddTransient<IWidget>(x => new AWidget());
        // Can't tell that it's an AWidget, so add
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeTrue();

        services = new ServiceCollection();
        services.AddTransient<IWidget, AWidget>();
        scanner.ShouldAdd(services, typeof(IWidget), typeof(AWidget)).ShouldBeFalse();
    }
}

public class BWidget : AWidget
{
}

public class CWidget : AWidget
{
}

public class DefaultWidget : AWidget
{
}