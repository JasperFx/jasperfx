using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JasperFx.CodeGeneration.Model;

internal class MethodFrameArranger : IMethodVariables
{
    private readonly IGeneratedMethod _method;
    private readonly IServiceVariableSource? _services;
    private readonly IGeneratedType _type;
    private readonly Dictionary<Type, Variable> _variables = new();

    public MethodFrameArranger(IGeneratedMethod method, IGeneratedType type, IServiceVariableSource? services) :
        this(method, type)
    {
        _services = services;
    }

    public MethodFrameArranger(IGeneratedMethod method, IGeneratedType type)
    {
        _method = method;
        _type = type;
    }

    public Variable FindVariable(ParameterInfo parameter)
    {
        if (_services != null && parameter.TryGetAttribute<FromKeyedServicesAttribute>(out var att))
        {
            if (_services.TryFindKeyedService(parameter.ParameterType, att.Key.ToString(), out var serviceVariable))
            {
                return serviceVariable!;
            }
        }
        
        if (TryFindVariableByName(parameter.ParameterType, parameter.Name!, out var variable))
        {
            return variable;
        }

        return FindVariable(parameter.ParameterType);
    }

    public Variable FindVariableByName(Type dependency, string name)
    {
        if (TryFindVariableByName(dependency, name, out var variable))
        {
            return variable;
        }

        throw new UnResolvableVariableException(dependency, name, _method);
    }

    public Variable FindVariable(Type type)
    {
        if (_variables.TryGetValue(type, out var value))
        {
            return value;
        }

        var variable = findVariable(type, VariableSource.All);
        if (variable == null)
        {
            throw new UnResolvableVariableException(type, _method);
        }

        _variables.Add(type, variable);

        return variable;
    }


    public bool TryFindVariableByName(Type dependency, string name, [NotNullWhen(true)] out Variable? variable)
    {
        variable = null;

        // It's fine here for now that we aren't looking through the services for
        // variables that could potentially be built by the IoC container
        var sourced = _method.Sources.Where(x => x.Matches(dependency)).Select(x => x.Create(dependency));
        var created = _method.Frames.SelectMany(x => x.Creates);

        var candidate = _variables.Values
            .Concat(_method.Arguments)
            .Concat(_method.DerivedVariables)
            .Concat(created)
            .Concat(sourced)
            .Where(x => x != null)
            .FirstOrDefault(x => x.VariableType == dependency && x.Usage == name);


        if (candidate != null)
        {
            variable = candidate;
            return true;
        }

        return false;
    }

    public Variable? TryFindVariable(Type type, VariableSource source)
    {
        if (_variables.TryGetValue(type, out var value))
        {
            return value;
        }

        var variable = findVariable(type, source);
        if (variable != null)
        {
            _variables.Add(type, variable);
        }

        return variable;
    }

    public void Arrange(out AsyncMode asyncMode, out Frame topFrame)
    {
        var compiled = compileFrames(_method.Frames);
        asyncMode = AsyncMode.AsyncTask;

        if (compiled.All(x => !x.IsAsync))
        {
            asyncMode = AsyncMode.None;
        }
        else if (compiled.Count(x => x.IsAsync) == 1 && compiled.Last().IsAsync && compiled.Last().CanReturnTask())
        {
            asyncMode = compiled.Any(x => x.Wraps) ? AsyncMode.AsyncTask : AsyncMode.ReturnFromLastNode;
        }

        topFrame = chainFrames(compiled);
    }


    protected Frame chainFrames(Frame[] frames)
    {
        // Step 5, put into a chain.
        for (var i = 1; i < frames.Length; i++)
        {
            frames[i - 1].Next = frames[i];
        }

        return frames[0];
    }

    protected Frame[] compileFrames(IList<Frame> frames)
    {
        // Step 1, resolve all the necessary variables
        foreach (var frame in frames) frame.ResolveVariables(this);

        // Step 1a;) -- figure out if you can switch to inline service
        // creation instead of the container.
        _services?.ReplaceVariables(this);

        // Step 2, calculate dependencies
        var dependencies = new DependencyGatherer(this, frames);
        findInjectedFields(dependencies);
        findSetters(dependencies);

        // Step 3, gather any missing frames and
        // add to the beginning of the list
        dependencies.Dependencies.SelectMany(x => x).Distinct()
            .Where(x => !frames.Contains(x))
            .Each(x => frames.Insert(0, x));

        // Step 4, topological sort in dependency order
        return frames.TopologicalSort(x => dependencies.Dependencies[x].GetEnumerator()).ToArray();
    }

    internal void findInjectedFields(DependencyGatherer dependencies)
    {
        foreach (var field in dependencies.Variables.Keys().OfType<InjectedField>())
        {
            _type.AllInjectedFields.Fill(field);
        }
    }

    internal void findSetters(DependencyGatherer dependencies)
    {
        foreach (var VARIABLE in dependencies.Variables)
        {
        }

        dependencies.Variables.Keys().Each((key, _) =>
        {
            if (key is Setter setter)
            {
                _type.Setters.Fill(setter);
            }
        });
    }

    private IEnumerable<IVariableSource> allVariableSources(VariableSource variableSource)
    {
        foreach (var source in _method.Sources) yield return source;

        foreach (var source in _type.Rules.Sources) yield return source;

        // To get injected fields
        if (_type is IVariableSource fields)
        {
            yield return fields;
        }

        if (variableSource == VariableSource.All && _services != null)
        {
            yield return _services;
        }
    }


    private Variable? findVariable(Type type, VariableSource variableSource)
    {
        var argument = _method.Arguments.Concat(_method.DerivedVariables).FirstOrDefault(x => x.VariableType == type);
        if (argument != null)
        {
            return argument;
        }

        var created = _method.Frames.SelectMany(x => x.Creates).FirstOrDefault(x => x.VariableType == type);
        if (created != null)
        {
            return created;
        }

        var source = allVariableSources(variableSource).FirstOrDefault(x => x.Matches(type));
        return source?.Create(type);
    }
}