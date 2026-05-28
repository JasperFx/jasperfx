using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;

namespace JasperFx.CodeGeneration;

internal class DependencyGatherer
{
    private readonly IMethodVariables _methodVariables;

    public readonly LightweightCache<Frame, List<Frame>> Dependencies = new();
    public readonly LightweightCache<Variable, List<Frame>> Variables = new();

    public DependencyGatherer(IMethodVariables methodVariables, IList<Frame> frames)
    {
        _methodVariables = methodVariables;
        Dependencies.OnMissing = frame =>
        {
            // Iterative BFS replaces the original co-recursive yield iterators that lacked cycle
            // protection and stack-overflowed on deep Frame/Variable graphs (e.g. Lamar/Wolverine's
            // resolver code-gen). Variables encountered along the way are pinned into the Variables
            // cache via Fill so MethodFrameArranger.findInjectedFields (which reads Variables.Keys())
            // observes the same population the original recursive walk produced as a side effect.
            var result = new List<Frame>();
            var seenFrames = new HashSet<Frame> { frame };
            var seenVariables = new HashSet<Variable>();
            Walk(frame, null, result, seenFrames, seenVariables, top: frame);
            return result;
        };
        Variables.OnMissing = v =>
        {
            var result = new List<Frame>();
            var seenFrames = new HashSet<Frame>();
            var seenVariables = new HashSet<Variable> { v };
            Walk(null, v, result, seenFrames, seenVariables, top: null);
            return result;
        };

        foreach (var frame in frames) Dependencies.FillDefault(frame);
    }

    private void Walk(Frame? startFrame, Variable? startVariable, List<Frame> result,
        HashSet<Frame> seenFrames, HashSet<Variable> seenVariables, Frame? top)
    {
        var frameQueue = new Queue<Frame>();
        var variableQueue = new Queue<Variable>();
        if (startFrame != null) frameQueue.Enqueue(startFrame);
        if (startVariable != null) variableQueue.Enqueue(startVariable);

        while (frameQueue.Count > 0 || variableQueue.Count > 0)
        {
            while (frameQueue.Count > 0)
            {
                var f = frameQueue.Dequeue();
                f.ResolveVariables(_methodVariables);

                foreach (var dep in f.Dependencies)
                {
                    if (seenFrames.Add(dep))
                    {
                        if (!ReferenceEquals(dep, top)) result.Add(dep);
                        frameQueue.Enqueue(dep);
                    }
                }

                foreach (var v in f.Uses)
                {
                    if (seenVariables.Add(v))
                    {
                        // Pin the key so findInjectedFields' Variables.Keys() lookup sees it. The
                        // value here is a placeholder; the next caller to read Variables[v]
                        // through the indexer triggers a fresh BFS via OnMissing.
                        Variables.Fill(v, EmptyList);
                        variableQueue.Enqueue(v);
                    }
                }
            }

            if (variableQueue.Count == 0) break;
            var variable = variableQueue.Dequeue();

            if (variable.Creator != null && seenFrames.Add(variable.Creator))
            {
                if (!ReferenceEquals(variable.Creator, top)) result.Add(variable.Creator);
                frameQueue.Enqueue(variable.Creator);
            }

            foreach (var d in variable.Dependencies)
            {
                if (seenVariables.Add(d))
                {
                    Variables.Fill(d, EmptyList);
                    variableQueue.Enqueue(d);
                }
            }
        }
    }

    private static readonly List<Frame> EmptyList = new(0);
}
