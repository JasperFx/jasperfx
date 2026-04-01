# Reflection Extensions

JasperFx.Core.Reflection provides helpers for type inspection, generic type manipulation, and readable type name generation.

## Usage

```csharp
using JasperFx.Core.Reflection;
```

## Examples

<!-- snippet: sample_reflection_extensions -->
<a id='snippet-sample_reflection_extensions'></a>
```cs
public void ReflectionHelpers()
{
    // Check if a type implements an interface
    var implements = typeof(List<string>).CanBeCastTo<IEnumerable<string>>();

    // Get a human-readable type name
    var name = typeof(Dictionary<string, int>).FullNameInCode();

    // Close an open generic type
    var closed = typeof(List<>).CloseAndBuildAs<object>(typeof(string));
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/ExtensionSamples.cs#L40-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_reflection_extensions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Available Methods

| Method | Description |
|--------|-------------|
| `Implements<T>()` | Checks if a type implements an interface or base class |
| `Implements(type)` | Non-generic overload of the above |
| `FullNameInCode()` | Returns a C#-readable full type name with generics |
| `NameInCode()` | Returns a short C#-readable type name |
| `CloseAndBuildAs<T>(params)` | Closes an open generic and creates an instance |
| `HasAttribute<T>()` | Checks if a member has a specific attribute |
| `GetAttribute<T>()` | Gets a specific attribute from a member |
| `IsConcreteTypeOf<T>()` | Checks if a type is a non-abstract implementation |

## Readable Type Names

`FullNameInCode()` produces names that look like actual C# code:

```csharp
typeof(int).FullNameInCode();                    // "int"
typeof(string[]).FullNameInCode();                // "string[]"
typeof(Dictionary<string, int>).FullNameInCode(); // "System.Collections.Generic.Dictionary<string, int>"
typeof(int?).FullNameInCode();                    // "int?"
```

## Closing Open Generics

`CloseAndBuildAs<T>` is useful for plugin architectures:

```csharp
// Given: public class Repository<T> : IRepository<T> { }
var repo = typeof(Repository<>)
    .CloseAndBuildAs<IRepository<Order>>(typeof(Order));
```

## Next Steps

- [String Extensions](/extensions/string-extensions)
- [Enumerable Extensions](/extensions/enumerable-extensions)
