using System.Reflection;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration.Model;

public class MemberAccessVariable : Variable
{
    private readonly Variable _parent;

    public MemberAccessVariable(Variable parent, MemberInfo member) : base(member.GetMemberType()!, parent.Creator!)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Member = member ?? throw new ArgumentNullException(nameof(member));
    }

    public MemberInfo Member { get; }

    public override string Usage => $"{_parent?.Usage}.{Member?.Name}";
}