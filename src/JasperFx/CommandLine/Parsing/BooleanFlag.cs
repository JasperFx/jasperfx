﻿using System.Reflection;

namespace JasperFx.CommandLine.Parsing;

public class BooleanFlag : TokenHandlerBase
{
    private readonly MemberInfo _member;

    public BooleanFlag(MemberInfo member) : base(member)
    {
        _member = member;
    }

    public override bool Handle(object input, Queue<string> tokens)
    {
        if (!tokens.NextIsFlagFor(_member))
        {
            return false;
        }

        tokens.Dequeue();
        setValue(input, true);

        return true;
    }

    public override string ToUsageDescription()
    {
        return $"[{InputParser.ToFlagAliases(_member)}]";
    }
}