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
    }
    
}