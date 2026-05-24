using System.Diagnostics.CodeAnalysis;
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
    /// Configure the "Critter Stack" defaults for service name, resource auto create, application assembly,
    /// and other shared settings. Same functionality as AddJasperFx()
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <remarks>
    /// This method is annotation-free for trim/AOT consumers: the only trim-unsafe
    /// work is the assembly-scan fallback in <see cref="CommandFactory.RegisterCommands(Assembly)"/>,
    /// which is gated behind the source-generated <c>DiscoveredCommands</c> manifest
    /// (emitted by the <c>JasperFx.SourceGenerator</c> analyzer). When the manifest
    /// is present at runtime, registration is fully trim-clean. When it is absent,
    /// the fallback path still works for runtime-codegen apps but will emit IL2026
    /// at the inner <c>RegisterCommands</c> call site. See <see cref="CommandLineHostingExtensions.ApplyFactoryDefaults"/>
    /// for the manifest-first short-circuit.
    /// </remarks>
    public static IServiceCollection CritterStackDefaults(this IServiceCollection services,
        Action<JasperFxOptions> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        return services.AddJasperFx(configure);
    }

    /// <summary>
    /// Configure JasperFx and Critter Stack tool behavior for resource management at runtime
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure">Optional configuration of the JasperFxDefaults for resource management</param>
    /// <returns></returns>
    /// <remarks>
    /// Annotation-free for trim/AOT — see the remarks on <see cref="CritterStackDefaults"/>.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Two reachable RUC call sites are stack-walk fallbacks that only execute when the consumer hasn't pre-set the application assembly: DetermineCallingAssembly is guarded by `RememberedApplicationAssembly == null`, and ReadHostEnvironment's establishApplicationAssembly fallback is guarded by `ApplicationAssembly == null`. AOT consumers are expected to call `JasperFxOptions.SetApplicationProject(typeof(Program).Assembly)` before AddJasperFx per the AOT publishing guide, which short-circuits both fallbacks. Apps that omit that call are by definition on the runtime-codegen path. See docs/codegen/aot.md.")]
    public static IServiceCollection AddJasperFx(this IServiceCollection services, Action<JasperFxOptions>? configure = null)
    {
        // It's actually important to do this as close as possible to this call
        if (JasperFxOptions.RememberedApplicationAssembly == null)
        {
            JasperFxOptions.RememberedApplicationAssembly = JasperFxOptions.DetermineCallingAssembly();
        }
        
        bool exists = services.Any(x => !x.IsKeyedService && x.ServiceType == typeof(JasperFxOptions));
        
        var optionsBuilder = services.AddOptions<JasperFxOptions>();
        
        if (configure != null)
        {
            optionsBuilder.Configure(configure!);
        }
        
        services.TryAddSingleton<IHostEnvironment, HostingEnvironment>();
        
        if (!exists)
        {
            optionsBuilder.PostConfigure<IHostEnvironment>((o, e) => o.ReadHostEnvironment(e));
            services.AddSingleton<JasperFxOptions>(s =>
            {
                return s.GetRequiredService<IOptions<JasperFxOptions>>().Value;
            });

            services.AddSingleton<ISystemPart>(s => s.GetRequiredService<JasperFxOptions>());
        }
        
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