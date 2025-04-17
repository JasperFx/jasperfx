using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace JasperFx.CommandLine.Descriptions;

public static class DescriptionExtensions
{
    /// <summary>
    ///     Register a JasperFx part description for console diagnostics
    /// </summary>
    /// <param name="services"></param>
    /// <param name="described"></param>
    public static void AddDescription(this IServiceCollection services, ISystemPart described)
    {
        services.AddSingleton(described);
    }

    /// <summary>
    ///     Register an JasperFx part description for console diagnostics
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    public static void AddDescription<T>(this IServiceCollection services) where T : class, ISystemPart
    {
        services.AddSingleton<ISystemPart, T>();
    }

    /// <summary>
    ///     Register an JasperFx part description for console diagnostics
    /// </summary>
    /// <param name="services"></param>
    /// <param name="title"></param>
    /// <param name="describe"></param>
    /// <typeparam name="T"></typeparam>
    public static void Describe<T>(this IServiceCollection services, string title, Action<T, TextWriter> describe)
    {
        services.DescribeAsync<T>(title, (service, writer) =>
        {
            describe(service, writer);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    ///     Register an JasperFx part description for console diagnostics
    /// </summary>
    /// <param name="services"></param>
    /// <param name="title"></param>
    /// <param name="describe"></param>
    /// <typeparam name="T"></typeparam>
    public static void DescribeAsync<T>(this IServiceCollection services, string title,
        Func<T, TextWriter, Task> describe)
    {
        var part = new LambdaDescribedSystemPart<T>(title, describe);

        services.AddSingleton<ISystemPart>(part);
    }

    /// <summary>
    ///     Create a Spectre table output from a dictionary
    /// </summary>
    /// <param name="props"></param>
    /// <returns></returns>
    public static Table BuildTableForProperties(this IDictionary<string, object> props)
    {
        var table = new Table();
        table.AddColumn("Property");
        table.AddColumn("Value");

        foreach (var (key, value) in props) table.AddRow(key, value?.ToString() ?? string.Empty);

        return table;
    }
}