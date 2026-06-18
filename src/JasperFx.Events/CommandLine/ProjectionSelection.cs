using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;

namespace JasperFx.Events.CommandLine;

public class ProjectionSelection(EventStoreUsage storage)
{
    public EventStoreUsage Storage { get; } = storage;
    public List<SubscriptionDescriptor> Subscriptions { get; } = new();
    public List<string> DatabaseIdentifiers { get; } = new();

    public static IReadOnlyList<ProjectionSelection> Filter(IReadOnlyList<EventStoreUsage> usages, ProjectionInput input)
    {
        if (usages.Count > 1 && input.StoreFlag.IsNotEmpty())
        {
            if (Uri.TryCreate(input.StoreFlag, UriKind.Absolute, out var subjectUri))
            {
                usages = usages.Where(x => x.SubjectUri.Matches(subjectUri)).ToArray();
            }
            else
            {
                usages = usages.Where(x => x.SubjectUri.Scheme.EqualsIgnoreCase(input.StoreFlag)).ToArray();
            }

            if (usages.Count == 0) return [];
        }
        
        var list = new List<ProjectionSelection>();
        foreach (var usage in usages)
        {
            if (TryFilterUsage(usage, input, out var subscription))
            {
                list.Add(subscription!);
            }
        }

        return list;
    }

    public static bool TryFilterUsage(EventStoreUsage usage, ProjectionInput input, out ProjectionSelection? selection)
    {
        selection = default;
        
        var subscriptions = input.ProjectionFlag.IsEmpty()
            ? usage.Subscriptions.ToArray()
            : usage.Subscriptions.Where(x => x.Name.EqualsIgnoreCase(input.ProjectionFlag)).ToArray();

        if (!subscriptions.Any()) return false;
        
        selection = new ProjectionSelection(usage);
        selection.Subscriptions.AddRange(subscriptions);
        
        if (usage.Database.Cardinality == DatabaseCardinality.Single)
        {
            selection.DatabaseIdentifiers.Add(usage.Database.MainDatabase.Identifier);
            return true;
        }

        if (input.TenantFlag.IsNotEmpty())
        {
            var database = usage.Database.Databases.FirstOrDefault(x => x.TenantIds.Contains(input.TenantFlag));
            if (database == null)
            {
                return false;
            }

            selection.DatabaseIdentifiers.Add(database.Identifier);
            return true;
        }

        if (input.DatabaseFlag.IsNotEmpty())
        {
            var databases =
                usage.Database.Databases.Where(x => x.Identifier.EqualsIgnoreCase(input.DatabaseFlag)).ToArray();

            if (databases.Any())
            {
                selection.DatabaseIdentifiers.AddRange(databases.Select(x => x.Identifier));
                return true;
            }
        }
        else
        {
            selection.DatabaseIdentifiers.AddRange(usage.Database.Databases.Select(x => x.Identifier));
            return true;
        }

        return false;
    }

    public static IReadOnlyList<ProjectionSelection> FilterForAsyncOnly(IReadOnlyList<ProjectionSelection> selections)
    {
        foreach (var selection in selections)
        {
            selection.Subscriptions.RemoveAll(x => x.Lifecycle != ProjectionLifecycle.Async);
        }

        return selections.Where(x => x.Subscriptions.Any()).ToArray();
    }

    // marten#4718 backport of the 9.x/2.x fix (jasperfx#438 / marten#4711): the
    // `projections rebuild` path must skip event subscriptions and Live-lifecycle
    // projections. A subscription (SubscriptionType.Subscription) is Async, so its
    // name would otherwise be fed to the rebuild path -> RebuildProjectionAsync ->
    // TryFindProjection, which only searches projections and throws
    // "No registered projection matches the name '...'". Live projections likewise
    // can't be rebuilt by the async daemon. They still run continuously and still
    // appear in `projections list`; only rebuild skips them. A subscription name
    // passed explicitly to rebuild becomes a clean no-op instead of a throw.
    public IReadOnlyList<SubscriptionDescriptor> RebuildableSubscriptions()
    {
        return Subscriptions
            .Where(x => x.SubscriptionType != SubscriptionType.Subscription
                        && x.Lifecycle != ProjectionLifecycle.Live)
            .ToArray();
    }
}