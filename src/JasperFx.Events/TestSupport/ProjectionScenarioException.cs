using JasperFx.Core;

namespace JasperFx.Events.TestSupport;

/// <summary>
///     Thrown when a ProjectionScenario fails. Aggregates every step failure, and the message
///     lists each executed step with a FAILED marker on the ones that went wrong
/// </summary>
public class ProjectionScenarioException: AggregateException
{
    public ProjectionScenarioException(IReadOnlyList<string> descriptions, IEnumerable<Exception> exceptions): base(
        $"Event Projection Scenario Failure{System.Environment.NewLine}{descriptions.Join(System.Environment.NewLine)}",
        exceptions)
    {
    }
}
