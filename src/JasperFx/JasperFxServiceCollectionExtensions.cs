using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace JasperFx;

public static class JasperFxServiceCollectionExtensions
{
    /// <summary>
    /// Configure JasperFx and Critter Stack tool behavior for resource management at runtime
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure">Optional configuration of the JasperFxDefaults for resource management</param>
    /// <returns></returns>
    public static IServiceCollection AddJasperFx(this IServiceCollection services, Action<JasperFxOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<JasperFxOptions>();
        if (configure != null)
        {
            optionsBuilder.Configure(configure!);
        }
        
        services.TryAddSingleton<IHostEnvironment, HostingEnvironment>();
        
        optionsBuilder.PostConfigure<IHostEnvironment>((o, e) => o.ReadHostEnvironment(e));
        services.AddSingleton<JasperFxOptions>(s =>
        {
            return s.GetRequiredService<IOptions<JasperFxOptions>>().Value;
        });

        services.AddSingleton<ISystemPart>(s => s.GetRequiredService<JasperFxOptions>());
        
        services.TryAddScoped<ICommandCreator, DependencyInjectionCommandCreator>();

        services.TryAddScoped<ICommandFactory>(ctx =>
        {
            var creator = ctx.GetRequiredService<ICommandCreator>();
            var options = ctx.GetRequiredService<IOptions<JasperFxOptions>>().Value;

            var factory = new CommandFactory(creator);
            factory.ApplyFactoryDefaults(Assembly.GetEntryAssembly());
            options.Factory?.Invoke(factory);
            return factory;
        });

        services.TryAddScoped<CommandExecutor>();

        return services;
    }
}