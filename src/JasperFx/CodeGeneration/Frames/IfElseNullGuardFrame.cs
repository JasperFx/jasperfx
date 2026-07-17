using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.CodeGeneration.Model;

namespace JasperFx.CodeGeneration.Frames;

/// <summary>
/// Frame that executes different code based on whether or not the original variable
/// is null or not
/// </summary>
public class IfElseNullGuardFrame : Frame
{
    private readonly Frame[] _existsPath;
    private readonly Frame[] _nullPath;
    private readonly Variable _subject;

    public IfElseNullGuardFrame(Variable subject, Frame[] nullPath, Frame[] existsPath) : base(nullPath.Any(x => x.IsAsync) ||
        existsPath.Any(x => x.IsAsync))
    {
        _subject = subject;
        _nullPath = nullPath;
        _existsPath = existsPath;
        uses.Add(subject);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        IfStyle.If.Open(writer, $"{_subject.Usage} == null");

        foreach (var frame in _nullPath) frame.GenerateCode(method, writer);

        IfStyle.If.Close(writer);
        IfStyle.Else.Open(writer, null);

        foreach (var frame in _existsPath) frame.GenerateCode(method, writer);

        IfStyle.Else.Close(writer);

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // F# if/then/else is an expression: when it is the trailing position both branches yield
        // the method's return value; otherwise both branches must be unit. `else` dedents back to
        // align with `if` (no braces).
        // `isNull` carries a 'T : null constraint that F# class types do not satisfy unless they are
        // [<AllowNullLiteral>], so box the subject first to erase the constraint (jasperfx#513).
        writer.Write($"BLOCK:if isNull (box {_subject.FSharpUsage}) then");
        foreach (var frame in _nullPath) frame.GenerateFSharpCode(method, writer);
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        foreach (var frame in _existsPath) frame.GenerateFSharpCode(method, writer);
        writer.FinishBlock();

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var frame in _existsPath)
        {
            foreach (var variable in frame.FindVariables(chain))
            {
                if (_existsPath.Any(x => x.Creates.Contains(variable)))
                {
                    continue;
                }

                // Make this conditional??
                yield return variable;
            }
        }

        foreach (var frame in _nullPath)
        {
            foreach (var variable in frame.FindVariables(chain))
            {
                if (_nullPath.Any(x => x.Creates.Contains(variable)))
                {
                    continue;
                }

                yield return variable;
            }
        }
    }
    
    
    /// <summary>
    /// Execute a series of inner frames if the specified variable is not null
    /// </summary>
    public class IfNullGuardFrame : CompositeFrame
    {
        private readonly Variable _variable;

        public IfNullGuardFrame(Variable variable, params Frame[] inner) : base(inner)
        {
            _variable = variable ?? throw new ArgumentNullException(nameof(variable));
            uses.Add(variable);

            Inners = inner;
        }
    
        public IReadOnlyList<Frame> Inners { get; }

        protected override void generateCode(GeneratedMethod method, ISourceWriter writer, Frame inner)
        {
            writer.Write($"BLOCK:if ({_variable.Usage} != null)");
            inner.GenerateCode(method, writer);
            writer.FinishBlock();
        }

        protected override void generateFSharpCode(GeneratedMethod method, ISourceWriter writer, Frame inner)
        {
            // Boxed for the same 'T : null reason as IfElseNullGuardFrame above (jasperfx#513).
            writer.Write($"BLOCK:if not (isNull (box {_variable.FSharpUsage})) then");
            inner.GenerateFSharpCode(method, writer);
            writer.FinishBlock();
        }
    }
    
}