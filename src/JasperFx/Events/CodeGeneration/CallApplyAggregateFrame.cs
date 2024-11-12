using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.CodeGeneration;

public class CallApplyAggregateFrame: Frame
{
    private readonly Type _fullSessionType;
    private Variable _aggregate;
    private Variable _cancellation;
    private Variable _session;
    private Variable _usedEventOnCreate;
    private readonly Type _querySessionType;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="methods"></param>
    /// <param name="fullSessionType">"Fuller" session type just in case we need that</param>
    public CallApplyAggregateFrame(ApplyMethodCollection methods, Type fullSessionType): base(methods.IsAsync)
    {
        _fullSessionType = fullSessionType;
        AggregateType = methods.AggregateType;
        _querySessionType = methods.QuerySessionType;
    }

    public Type AggregateType { get; }
    public bool InsideForEach { get; set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _aggregate = chain.FindVariable(AggregateType);

        _session = chain.TryFindVariable(_querySessionType, VariableSource.All) ??
                   chain.FindVariable(_fullSessionType);

        _usedEventOnCreate = chain.FindVariableByName(typeof(bool), CallCreateAggregateFrame.UsedEventOnCreateName);

        yield return _session;

        if (IsAsync)
        {
            _cancellation = chain.TryFindVariable(typeof(CancellationToken), VariableSource.All) ??
                            new Variable(typeof(CancellationToken),
                                $"{typeof(CancellationToken).FullNameInCode()}.None");

            yield return _cancellation;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (InsideForEach)
        {
            writer.Write($"BLOCK:foreach (var @event in events.Skip({_usedEventOnCreate.Usage} ? 1 : 0))");
        }

        if (IsAsync)
        {
            writer.WriteLine(
                $"{_aggregate.Usage} = await {ApplyMethodCollection.MethodName}(@event, {_aggregate.Usage}, {_session.Usage}, {_cancellation.Usage});");
        }
        else
        {
            writer.WriteLine(
                $"{_aggregate.Usage} = {ApplyMethodCollection.MethodName}(@event, {_aggregate.Usage}, {_session.Usage});");
        }

        if (InsideForEach)
        {
            writer.FinishBlock();
        }

        Next?.GenerateCode(method, writer);
    }
}
