# Enumerable Extensions

JasperFx.Core provides extension methods for working with collections and enumerables.

## Usage

```csharp
using JasperFx.Core;
```

## Examples

<!-- snippet: sample_enumerable_extensions -->
<a id='snippet-sample_enumerable_extensions'></a>
```cs
public void EnumerableHelpers()
{
    var items = new List<string> { "a", "b", "c", "a" };

    // AddRange that works on IList
    items.Fill("d");

    // Add only if not already present
    items.Fill("a"); // no-op if already present

    // Each / EachAsync for side effects
    items.Each(item => Console.WriteLine(item));
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/ExtensionSamples.cs#L24-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enumerable_extensions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Available Methods

| Method | Description |
|--------|-------------|
| `Fill(item)` | Adds an item to a list (alias for `Add`) |
| `FillWithUnique(item)` | Adds only if the item is not already in the list |
| `Each(action)` | Executes an action for each item |
| `EachAsync(func)` | Executes an async function for each item sequentially |
| `AddRange(items)` | Adds multiple items to an `IList<T>` |
| `IsEmpty()` | Returns true if the enumerable has no elements |
| `Top(n)` | Returns the first N items |
| `As<T>()` | Casts each element to type T |

## Fill vs Add

`Fill` is semantically equivalent to `Add` but reads better in fluent chains. `FillWithUnique` is useful for building distinct collections without using `HashSet`:

```csharp
var tags = new List<string>();
tags.FillWithUnique("dotnet");
tags.FillWithUnique("dotnet"); // no duplicate added
```

## Each vs ForEach

`Each` works on any `IEnumerable<T>`, not just `List<T>`:

```csharp
IEnumerable<string> items = GetItems();
items.Each(item => Process(item));
```

## Next Steps

- [String Extensions](/extensions/string-extensions)
- [Reflection Extensions](/extensions/reflection-extensions)
