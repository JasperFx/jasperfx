using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace JasperFx.CodeGeneration;

public interface ICodeFile
{
    string FileName { get; }

    void AssembleTypes(GeneratedAssembly assembly);

    Task<bool> AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace);

    bool AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace);

    void AssertServiceLocationsAreAllowed(ServiceLocationReport[] reports, IServiceProvider? services)
    {
        if (reports.Any())
        {
            var logger = services.GetLoggerOrDefault<ICodeFile>();

            foreach (var report in reports)
            {
                if (report.ServiceDescriptor.IsKeyedService)
                {
                    logger.LogDebug(
                        "Utilizing service location for {CodeFile} for Service {ServiceType} ({Key}): {Reason}", this,
                        report.ServiceDescriptor.ServiceType, report.ServiceDescriptor.ServiceKey, report.Reason);
                }
                else
                {
                    logger.LogDebug("Utilizing service location for {CodeFile} for Service {ServiceType}: {Reason}",
                        this, report.ServiceDescriptor.ServiceType, report.Reason);
                }
            }
        }
    }

    bool TryReplaceServiceProvider(out Variable serviceProvider)
    {
        serviceProvider = default;
        return false;
    }
}