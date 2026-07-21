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
        Justification = "Two reachable RUC call sites are stack-walk fallbacks that only run on the runtime-codegen path: DetermineCallingAssembly (used to seed RememberedApplicationAssembly for the first host and, for GH-3521, to capture the registering assembly of every host) and ReadHostEnvironment's establishApplicationAssembly fallback, guarded by `ApplicationAssembly == null`. AOT consumers set the application assembly explicitly per the AOT publishing guide, which routes them through the `ApplicationAssembly != null` short-circuit; and under NativeAOT the frames carry no method metadata, so DetermineCallingAssembly falls back to Assembly.GetEntryAssembly() without throwing and its result is unused. See docs/codegen/aot.md.")]
    public static IServiceCollection AddJasperFx(this IServiceCollection services, Action<JasperFxOptions>? configure = null)
    {
        // GH-3521: capture the assembly this AddJasperFx call was made from while the caller's frame is
        // still on the stack. This lets a later host that adopts an application assembly pinned by an
        // EARLIER host in the same process (the classic multi-host test harness) warn instead of silently
        // scanning the wrong assembly. It's actually important to do this as close as possible to this call.
        var registrationAssembly = JasperFxOptions.DetermineCallingAssembly();
        if (JasperFxOptions.RememberedApplicationAssembly == null)
        {
            JasperFxOptions.RememberedApplicationAssembly = registrationAssembly;
        }

        bool exists = services.Any(x => !x.IsKeyedService && x.ServiceType == typeof(JasperFxOptions));
        
        var optionsBuilder = services.AddOptions<JasperFxOptions>();
        
        if (configure != null)
        {
            optionsBuilder.Configure(configure!);
        }

        // GH-3521: stash the registering assembly onto the options instance (the local is resolved eagerly
        // above; this lambda only assigns it). ??= keeps the first registration when AddJasperFx is called
        // more than once against the same container. Compared later in establishApplicationAssembly against
        // the assembly actually adopted for discovery.
        optionsBuilder.Configure(o => o.RegistrationCallingAssembly ??= registrationAssembly);

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