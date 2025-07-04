﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration.Expressions;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration.Model;

public class Variable
{
    // private static readonly string[] _reservedNames = new string[]
    //     { "lock", "switch", "case", "if", "base", "catch", "class", "continue", "default", "operator" };

    private static readonly string[] _reservedNames;

    static Variable()
    {
        _reservedNames = _reserved.ReadLines().Where(x => !x.IsEmpty()).ToArray();
    }

    private static readonly string _reserved = @"
abstract
as
base
bool
break
byte
case
catch
char
checked
class
const
continue
decimal
default
delegate
do
double
else
enum
event
explicit
extern
false
finally
fixed
float
for
foreach
goto
if
implicit
in
int
interface
internal
is
lock
long
namespace
new
null
object
operator
out
override
params
private
protected
public
readonly
ref
return
sbyte
sealed
short
sizeof
stackalloc
static
string
struct
switch
this
throw
true
try
typeof
uint
ulong
unchecked
unsafe
ushort
using
virtual
void
volatile
while
";
    
    private Frame? _frame;

    public Variable(Type variableType) : this(variableType, DefaultArgName(variableType))
    {
    }

    public Variable(Type variableType, string usage)
    {
        VariableType = variableType ?? throw new ArgumentNullException(nameof(variableType));
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));

        if (usage == "event") Usage = "@event";
    }

    public Variable(Type variableType, string usage, Frame? creator) : this(variableType, usage)
    {
        if (creator != null)
        {
            Creator = creator;

            Creator.creates.Fill(this);
        }
    }

    public Variable(Type variableType, Frame creator) : this(variableType, DefaultArgName(variableType), creator)
    {
    }

    public Frame? Creator
    {
        get => _frame;
        protected set
        {
            _frame = value;
            Creator?.creates.Fill(this);
        }
    }

    public Type VariableType { get; }
    public virtual string Usage { get; protected set; }

    /// <summary>
    ///     How the variable is used within assignments. Default is
    ///     $"var {Usage}"
    /// </summary>
    public virtual string AssignmentUsage => $"var {Usage}";

    public virtual string ArgumentDeclaration => Usage;

    /// <summary>
    ///     Used to smuggle additional information about a variable
    /// </summary>
    public Dictionary<string, object> Properties { get; } = new();

    /// <summary>
    ///     Marks other variables that this variable depends on
    /// </summary>
    public IList<Variable> Dependencies { get; } = new List<Variable>();

    public static Variable[] VariablesForProperties<T>(string rootArgName)
    {
        return typeof(T).GetTypeInfo().GetProperties().Where(x => x.CanRead)
            .Select(x => new Variable(x.PropertyType, $"{rootArgName}.{x.Name}"))
            .ToArray();
    }

    public static Variable For<T>(string? variableName = null)
    {
        return new Variable(typeof(T), variableName ?? DefaultArgName(typeof(T)));
    }

    public static string SanitizeVariableName(string variableName)
    {
        if (_reservedNames.Contains(variableName))
        {
            return "@" + variableName;
        }
        
        return variableName.Replace('<', '_').Replace('>', '_');
    }

    public static string DefaultArgName(Type argType)
    {
        if (argType.IsArray)
        {
            return DefaultArgName(argType.GetElementType()!) + "Array";
        }

        if (argType.IsEnumerable())
        {
            var argPrefix = DefaultArgName(argType.DetermineElementType()!);
            var suffix = argType.GetGenericTypeDefinition().Name.Split('`').First();

            return SanitizeVariableName(argPrefix + suffix);
        }

        if (argType == typeof(string)) return "stringValue";
        if (argType == typeof(decimal)) return "decimalValue";
        if (argType == typeof(bool)) return "boolValue";
        if (argType == typeof(int)) return "intValue";
        if (argType == typeof(long)) return "longValue";

        var parts = argType.Name.SplitPascalCase().Split(' ');
        if (argType.GetTypeInfo().IsInterface && parts.First() == "I")
        {
            parts = parts.Skip(1).ToArray();
        }

        var raw = (parts.First().ToLower() + parts.Skip(1).Join("")).Split('`').First();
        return SanitizeVariableName(raw);
    }

    public static string DefaultArgName<T>()
    {
        return DefaultArgName(typeof(T));
    }

    /// <summary>
    ///     On rare occasions you may need to override the variable name
    /// </summary>
    /// <param name="variableName"></param>
    public virtual void OverrideName(string variableName)
    {
        Usage = variableName;
    }

    /// <summary>
    ///     On rare occasions you may need to override the variable type
    /// </summary>
    /// <param name="variableType"></param>
    public void OverrideType(Type variableType)
    {
    }

    public override string ToString()
    {
        return $"{nameof(VariableType)}: {VariableType}, {nameof(Usage)}: {Usage}";
    }

    protected bool Equals(Variable other)
    {
        return VariableType == other.VariableType && string.Equals(Usage, other.Usage);
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

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((Variable)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return ((VariableType != null ? VariableType.GetHashCode() : 0) * 397) ^
                   (Usage != null ? Usage.GetHashCode() : 0);
        }
    }

    public virtual Expression ToVariableExpression(LambdaDefinition definition)
    {
        return Expression.Variable(VariableType, Usage);
    }
}