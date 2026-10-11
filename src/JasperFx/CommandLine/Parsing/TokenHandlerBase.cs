using System.Collections;
using System.Reflection;
using JasperFx.Core.Reflection;

namespace JasperFx.CommandLine.Parsing;

public abstract class TokenHandlerBase : ITokenHandler
{
    protected TokenHandlerBase(MemberInfo member)
    {
        Member = member;
    }

    /// <summary>
    /// Constructor for source-generated handlers that don't need MemberInfo.
    /// </summary>
    protected TokenHandlerBase(string memberName, string description)
    {
        _memberName = memberName;
        _description = description;
    }

    private readonly string? _memberName;
    private readonly string? _description;

    public MemberInfo? Member { get; }

    public string Description
    {
        get
        {
            if (_description != null) return _description;
            var name = Member!.Name;
            Member.ForAttribute<DescriptionAttribute>(att => name = att.Description);
            return name;
        }
    }

    public string MemberName => _memberName ?? Member!.Name;

    public abstract bool Handle(object input, Queue<string> tokens);
    public abstract string ToUsageDescription();

    protected void setValue(object target, object value)
    {
        (Member as PropertyInfo)?.SetValue(target, value);
        (Member as FieldInfo)?.SetValue(target, value);
    }

    // An enumerable flag or argument collects its values into a List<T>, which can't be assigned to a
    // T[] member, so an array member is handed them as an array (the source-generated parser's ToArray())
    protected void setEnumerableValue(object target, IList values)
    {
        var memberType = Member!.GetMemberType()!;
        if (memberType.IsArray)
        {
            var array = Array.CreateInstanceFromArrayType(memberType, values.Count);
            values.CopyTo(array, 0);
            setValue(target, array);
        }
        else
        {
            setValue(target, values);
        }
    }

    protected object? getValue(object target)
    {
        return (Member as PropertyInfo)?.GetValue(target)
               ?? (Member as FieldInfo)?.GetValue(target);
    }

    public bool Equals(TokenHandlerBase other)
    {
        if (ReferenceEquals(null, other))
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other.Member.Equals(Member);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != typeof(TokenHandlerBase))
        {
            return false;
        }

        return Equals((TokenHandlerBase)obj);
    }

    public override int GetHashCode()
    {
        return Member != null ? Member.GetHashCode() : 0;
    }
}