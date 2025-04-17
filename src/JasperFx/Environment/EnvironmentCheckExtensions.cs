using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Environment;

public static class EnvironmentCheckExtensions
{
    /// <summary>
    ///     Issue a check against the running environment asynchronously. Throw an
    ///     exception to denote environment failures
    /// </summary>
    /// <param name="services"></param>
    /// <param name="description"></param>
    /// <param name="test"></param>
    public static void CheckEnvironment(this IServiceCollection services,
        string description,
        Func<IServiceProvider, CancellationToken, Task> test)
    {
        services.AddJasperFx(opts => opts.RegisterEnvironmentCheck(description, test));
    }

    /// <summary>
    ///     Issue a check against the running environment synchronously. Throw an
    ///     exception to denote environment failures
    /// </summary>
    /// <param name="services"></param>
    /// <param name="description"></param>
    /// <param name="action"></param>
    public static void CheckEnvironment(this IServiceCollection services, string description,
        Action<IServiceProvider> action)
    {
        services.CheckEnvironment(description, (s, c) =>
        {
            action(s);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    ///     Issue a check against the running environment using a registered service of type T synchronously. Throw an
    ///     exception to denote environment failures
    /// </summary>
    /// <param name="services"></param>
    /// <param name="description"></param>
    /// <param name="action"></param>
    /// <typeparam name="T"></typeparam>
    public static void CheckEnvironment<T>(this IServiceCollection services, string description, Action<T?> action) where T: notnull
    {
        services.CheckEnvironment(description, (s, c) =>
        {
            action(s.GetService<T>());
            return Task.CompletedTask;
        });
    }


    /// <summary>
    ///     Issue a check against the running environment using a registered service of type T asynchronously. Throw an
    ///     exception to denote environment failures
    /// </summary>
    /// <param name="services"></param>
    /// <param name="description"></param>
    /// <param name="action"></param>
    /// <typeparam name="T"></typeparam>
    public static void CheckEnvironment<T>(this IServiceCollection services, string description,
        Func<T?, CancellationToken, Task> action) where T: notnull
    {
        services.CheckEnvironment(description, async (s, c) => { await action(s.GetService<T>(), c); });
    }

    #region sample_CheckThatFileExists

    /// <summary>
    ///     Issue an environment check for the existence of a named file
    /// </summary>
    /// <param name="services"></param>
    /// <param name="path"></param>
    public static void CheckThatFileExists(this IServiceCollection services, string path)
    {
        services.AddJasperFx(opts => opts.RequireFile(path));
    }

    #endregion

    #region sample_CheckServiceIsRegistered

    /// <summary>
    ///     Issue an environment check for the registration of a service in the underlying IoC
    ///     container
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    public static void CheckServiceIsRegistered<T>(this IServiceCollection services) where T: notnull
    {
        services.CheckEnvironment($"Service {typeof(T).FullName} should be registered", s => s.GetRequiredService<T>());
    }

    /// <summary>
    ///     Issue an environment check for the registration of a service in the underlying IoC
    ///     container
    /// </summary>
    /// <param name="services"></param>
    /// <param name="serviceType"></param>
    public static void CheckServiceIsRegistered(this IServiceCollection services, Type serviceType)
    {
        services.CheckEnvironment($"Service {serviceType.FullName} should be registered",
            s => s.GetRequiredService(serviceType));
    }

    #endregion
}