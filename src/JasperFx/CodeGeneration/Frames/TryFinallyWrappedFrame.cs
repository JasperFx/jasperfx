using JasperFx.CodeGeneration.Model;

namespace JasperFx.CodeGeneration.Frames;

/// <summary>
///     Wraps a single frame just inside of a dedicated try/finally block, with the
///     "finallys" executed in the finally{} block.
/// </summary>
public class TryFinallyWrapperFrame : Frame
{
    private readonly Frame[] _finallys;
    private readonly Frame _inner;

    public TryFinallyWrapperFrame(Frame inner, Frame[] finallys) : base(inner.IsAsync)
    {
        _inner = inner;
        _finallys = finallys;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        _inner.GenerateCode(method, writer);
        writer.Write("BLOCK:try");

        Next?.GenerateCode(method, writer);

        writer.FinishBlock();
        writer.Write("BLOCK:finally");

        if (_finallys.Length > 1)
        {
            for (var i = 1; i < _finallys.Length; i++)
            {
                _finallys[i - 1].Next = _finallys[i];
            }
        }

        _finallys[0].GenerateCode(method, writer);

        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in _inner.FindVariables(chain)) yield return variable;

        // NOT letting the finallys get involved with ordering frames
        // because that way lies madness
        foreach (var @finally in _finallys)
        {
            @finally
                // Forcing it to evaluate and attach all variables
                .FindVariables(chain).ToArray();
        }
    }
}