using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.Core.IoC;

internal static class ScanningExploder
{
    internal static (IServiceCollection, AssemblyScanner[]) ExplodeSynchronously(IServiceCollection services)
    {
        var scanners = new AssemblyScanner[0];
        IServiceCollection registry = new ServiceCollection();
        registry.AddRange(services);

        var registriesEncountered = new List<Type>();

        while (registry.HasScanners())
        {
            var (registry2, operations) = ParseToOperations(registry, registriesEncountered);

            var additional = operations.OfType<AssemblyScanner>().ToArray();

            registry = registry2;
            scanners = scanners.Concat(additional).ToArray();

            registry.RemoveAll(x => x.ServiceType == typeof(AssemblyScanner));

            foreach (var scanner in additional)
            {
                scanner.Start();
            }

            foreach (var operation in operations)
            {
                if (operation is AssemblyScanner scanner)
                {
                    scanner.ApplyRegistrations(registry);
                }

                if (operation is ServiceDescriptor[] descriptors)
                {
                    registry.AddRange(descriptors);
                }
            }
        }

        return (registry, scanners);
    }

    internal static Task<(IServiceCollection, AssemblyScanner[])> Explode(IServiceCollection services)
    {
        var scanners = Array.Empty<AssemblyScanner>();
        IServiceCollection registry = services.ToCollection();

        var registriesEncountered = new List<Type>();

        while (registry.HasScanners())
        {
            var (registry2, operations) = ParseToOperations(registry, registriesEncountered);

            var additional = operations.OfType<AssemblyScanner>().ToArray();

            registry = registry2;
            scanners = scanners.Concat(additional).ToArray();

            registry.RemoveAll(x => x.ServiceType == typeof(AssemblyScanner));

            foreach (var operation in operations)
            {
                if (operation is AssemblyScanner scanner)
                {
                    scanner.Start();
                    scanner.ApplyRegistrations(registry);
                }

                if (operation is ServiceDescriptor[] descriptors)
                {
                    registry.AddRange(descriptors);
                }
            }
        }

        return Task.FromResult<(IServiceCollection, AssemblyScanner[])>((registry, scanners));
    }


    private static (IServiceCollection, List<object>) ParseToOperations(IServiceCollection services,
        List<Type> registriesEncountered)
    {
        var scanners = services
            .Where(x => x.ServiceType == typeof(AssemblyScanner))
            .ToArray();

        var indexes = scanners
            .Select(services.IndexOf)
            .ToArray();

        var operations = new List<object>();

        var initial = indexes[0] > 0
            ? services.Take(indexes[0]).ToCollection()
            : new ServiceCollection();

        operations.Add(scanners[0].ImplementationInstance);

        for (var i = 1; i < indexes.Length; i++)
        {
            var index = indexes[i];
            var previous = indexes[i - 1];

            if (previous != index - 1)
            {
                // they are not sequential, just add a Scan operation
                var slice = services.Skip(previous + 1).Take(index - previous - 1).ToArray();
                operations.Add(slice);
            }


            operations.Add(scanners[i].ImplementationInstance);
        }

        // Are there more?
        if (indexes.Last() != services.Count - 1)
        {
            operations.Add(services.Skip(indexes.Last() + 1).ToArray());
        }

        return (initial, operations);
    }
}