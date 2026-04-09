namespace JasperFx.CommandLine.Parsing.Generated;

/// <summary>
/// Source-generated enumerable argument handler. Replaces EnumerableArgument
/// with direct setter and converter delegates.
/// </summary>
public sealed class GeneratedEnumerableArgument<T> : Argument
{
    private readonly Action<object, List<T>> _setter;
    private readonly Func<string, T> _converter;

    public GeneratedEnumerableArgument(string memberName, string description,
        Action<object, List<T>> setter, Func<string, T> converter)
        : base(memberName, description)
    {
        _setter = setter;
        _converter = converter;
    }

    public override bool Handle(object input, Queue<string> tokens)
    {
        var list = new List<T>();
        var wasHandled = false;

        while (tokens.Count > 0 && !tokens.NextIsFlag())
        {
            var value = _converter(tokens.Dequeue());
            list.Add(value);
            wasHandled = true;
        }

        if (wasHandled)
        {
            _setter(input, list);
        }

        return wasHandled;
    }

    public override string ToUsageDescription()
    {
        var name = MemberName.ToLower();
        return $"<{name}1 {name}2 {name}3 ...>";
    }
}
