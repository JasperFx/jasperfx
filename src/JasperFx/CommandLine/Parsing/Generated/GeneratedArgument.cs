using JasperFx.Core;

namespace JasperFx.CommandLine.Parsing.Generated;

/// <summary>
/// Source-generated positional argument handler. Replaces Argument by using a
/// direct setter delegate and inlined converter instead of reflection.
/// Extends Argument so that UsageGraph's OfType&lt;Argument&gt;() filtering works.
/// </summary>
public sealed class GeneratedArgument<T> : Argument
{
    private readonly Action<object, T> _directSetter;
    private readonly Func<string, T> _directConverter;
    private readonly bool _isEnum;
    private bool _isLatched;
    private readonly bool _isNumeric;

    public GeneratedArgument(string memberName, string description,
        Action<object, T> setter, Func<string, T> converter, bool isEnum = false, bool isNumeric = false)
        : base(memberName, description)
    {
        _directSetter = setter;
        _directConverter = converter;
        _isEnum = isEnum;
        _isNumeric = isNumeric;
    }

    public override bool Handle(object input, Queue<string> tokens)
    {
        if (_isLatched)
            return false;

        if (tokens.NextIsFlag())
        {
            if (_isNumeric)
            {
                if (!decimal.TryParse(tokens.Peek(), out _))
                    return false;
            }
            else
            {
                return false;
            }
        }

        var value = _directConverter(tokens.Dequeue());
        _directSetter(input, value);
        _isLatched = true;
        return true;
    }

    public override string ToUsageDescription()
    {
        if (_isEnum)
        {
            return Enum.GetNames(typeof(T)).Join("|");
        }

        return $"<{MemberName.ToLower()}>";
    }
}
