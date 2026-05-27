using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Services;

internal class ScopedContainerCreation : SyncFrame, IScopedContainerCreation
{
    private readonly List<SyncFrame> _postprocessors = new();

    public ScopedContainerCreation()
    {
        Factory = new InjectedField(typeof(IServiceScopeFactory), "serviceScopeFactory");
        Scope = new Variable(typeof(IServiceScope), "serviceScope", this);
        Scoped = new Variable(typeof(IServiceProvider), $"{Scope.Usage}.{nameof(AsyncServiceScope.ServiceProvider)}", this);
    }

    public Variable Scope { get; }
    public Variable Factory { get; }
    public Variable Scoped { get; }

    /// <summary>
    ///     Register a synchronous frame to emit right after the scope-creation line and before
    ///     <see cref="Frame.Next" />, in registration order. See <see cref="IScopedContainerCreation" />.
    /// </summary>
    public void AddPostProcessor(SyncFrame frame)
    {
        _postprocessors.Add(frame);
    }

    // Surface the postprocessors' created variables alongside this frame's own Scope/Scoped so they
    // are visible to downstream Next frames. Each surfaced variable is RE-PARENTED to this frame so
    // the arranger orders a downstream consumer after this (top-level) frame, and does NOT insert the
    // nested postprocessor as a duplicate top-level frame — which would chain back into this frame's
    // own postprocessor emission and recurse infinitely (jasperfx#385). The re-parent is idempotent.
    public override IEnumerable<Variable> Creates
    {
        get
        {
            var created = _postprocessors.SelectMany(x => x.Creates).ToArray();
            foreach (var variable in created)
            {
                variable.OverrideCreator(this);
            }

            return created.Concat([Scope, Scoped]).ToArray();
        }
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return Factory;

        foreach (var postprocessor in _postprocessors)
        {
            // Hand our scoped provider to the child BEFORE it resolves its own variables, so it
            // never yields a request for IServiceProvider (which the arranger would satisfy by
            // spinning up another scope — a bi-directional dependency).
            if (postprocessor is IUsesServiceProviderFrame usesProvider)
            {
                usesProvider.UseServiceProvider(Scoped);
            }

            foreach (var variable in postprocessor.FindVariables(chain))
            {
                yield return variable;
            }
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // #228: gate the `await using` emission on AsyncMode == AsyncTask
        // (not `!= None`). `ReturnFromLastNode` and `ReturnCompletedTask`
        // both have non-async method *declarations* (no `async` keyword)
        // that just return a Task — emitting `await using` in those bodies
        // is a CS4032/CS1996 compile error. AsyncTask is the only mode that
        // also produces the `async ReturnType` declaration (see
        // GeneratedMethod.determineReturnExpression), so it's the only mode
        // where `await using` is valid.
        if (method.AsyncMode == AsyncMode.AsyncTask)
        {
            writer.Write(
                $"await using var {Scope.Usage} = {Factory.Usage}.{nameof(ServiceProviderServiceExtensions.CreateAsyncScope)}();");
        }
        else
        {
            writer.Write(
                $"using var {Scope.Usage} = {Factory.Usage}.{nameof(IServiceScopeFactory.CreateScope)}();");
        }

        // Postprocessors run immediately after the scope line and before Next, in registration
        // order. Chain them like CompositeFrame so each delegates to the next; the last one's Next
        // stays null so the chain stops before our own Next.
        if (_postprocessors.Count > 0)
        {
            for (var i = 1; i < _postprocessors.Count; i++)
            {
                _postprocessors[i - 1].Next = _postprocessors[i];
            }

            _postprocessors[0].GenerateCode(method, writer);
        }

        Next?.GenerateCode(method, writer);
    }
}
