using System.Threading.Tasks.Dataflow;

namespace JasperFx.Core;

public static class BlockExtensions
{
    public static ExecutionDataflowBlockOptions SequentialOptions(this CancellationToken token)
    {
        return new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true, MaxDegreeOfParallelism = 1, CancellationToken = token
        };
    }
}
