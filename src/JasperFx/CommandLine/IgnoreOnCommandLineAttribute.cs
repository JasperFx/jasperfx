﻿namespace JasperFx.CommandLine;

/// <summary>
///     Oakton ignores any fields or properties with this attribute during the binding to the input
///     objects
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class IgnoreOnCommandLineAttribute : Attribute
{
}