# String Extensions

JasperFx.Core provides several extension methods for common string operations.

## Usage

```csharp
using JasperFx.Core;
```

## Examples

<!-- snippet: sample_string_extensions -->
<a id='snippet-sample_string_extensions'></a>
```cs
public void StringHelpers()
{
    // Convert to camel case
    var camel = "SomePropertyName".ToCamelCase();
    // => "somePropertyName"

    // Check if a string is empty or whitespace
    var isEmpty = "".IsEmpty();
    var isNotEmpty = "hello".IsNotEmpty();

    // Joining strings
    var joined = new[] { "one", "two", "three" }.Join(", ");
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/ExtensionSamples.cs#L8-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_string_extensions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Available Methods

| Method | Description |
|--------|-------------|
| `ToCamelCase()` | Converts PascalCase to camelCase |
| `IsEmpty()` | Returns true if the string is null, empty, or whitespace |
| `IsNotEmpty()` | Inverse of `IsEmpty()` |
| `Join(separator)` | Joins an enumerable of strings with a separator |
| `ToDelimitedString(delimiter)` | Converts a collection to a delimited string |
| `EqualsIgnoreCase(other)` | Case-insensitive string comparison |
| `Matches(pattern)` | Regex match helper |
| `SplitOnNewLine()` | Splits on `\n` and `\r\n` |
| `ToFullPath()` | Resolves a relative path to a full filesystem path |

## Null Safety

Most string extensions handle null input gracefully:

```csharp
string? value = null;
value.IsEmpty();    // true
value.IsNotEmpty(); // false
```

## Next Steps

- [Enumerable Extensions](/extensions/enumerable-extensions)
- [Reflection Extensions](/extensions/reflection-extensions)
