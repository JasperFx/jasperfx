# Variables

The `Variable` class in `JasperFx.CodeGeneration.Model` represents a named, typed value flowing through generated code. Variables are the primary mechanism frames use to declare their inputs and outputs, enabling the code generation engine to resolve dependencies and order frames automatically.

## Creating Variables

<!-- snippet: sample_variable_creation -->
<a id='snippet-sample_variable_creation'></a>
```cs
public static void CreateVariables()
{
    // Create a variable with an auto-generated name based on the type
    var widget = new Variable(typeof(Widget), "widget");

    // The Usage property is the C# identifier used in generated code
    Console.WriteLine(widget.Usage); // "widget"

    // DefaultArgName generates a camelCase name from the type
    var defaultName = Variable.DefaultArgName(typeof(Widget));
    Console.WriteLine(defaultName); // "widget"

    // Variable tied to a creating Frame
    var frame = new MethodCall(typeof(WidgetFactory), nameof(WidgetFactory.Build));
    var returnVar = frame.ReturnVariable;
    Console.WriteLine(returnVar!.Creator == frame); // true
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/VariablesSamples.cs#L9-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_variable_creation' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Key Properties

| Property | Type | Description |
|----------|------|-------------|
| `VariableType` | `Type` | The .NET type of the variable. |
| `Usage` | `string` | The C# identifier used in generated source code. |
| `Creator` | `Frame?` | The frame that creates this variable, if any. |

## Default Naming

`Variable.DefaultArgName(Type)` produces a conventional camelCase name from a type:

<!-- snippet: sample_variable_default_arg_names -->
<a id='snippet-sample_variable_default_arg_names'></a>
```cs
public static void DefaultArgNameExamples()
{
    // Simple types use lowercase type name
    Console.WriteLine(Variable.DefaultArgName(typeof(Widget))); // "widget"

    // Arrays get "Array" suffix
    Console.WriteLine(Variable.DefaultArgName(typeof(int[]))); // "intArray"

    // Generic types include inner type
    Console.WriteLine(Variable.DefaultArgName(typeof(List<string>))); // "listOfString"

    // Reserved C# keywords get an @ prefix
    Console.WriteLine(Variable.DefaultArgName(typeof(Event))); // "@event"
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/VariablesSamples.cs#L31-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_variable_default_arg_names' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The naming rules are:

- Simple types use the lowercase type name (e.g., `Widget` becomes `widget`).
- Array types append `Array` (e.g., `int[]` becomes `intArray`).
- Generic types include the inner type (e.g., `List<string>` becomes `listOfString`).
- If the resulting name is a C# reserved keyword, it is prefixed with `@` (e.g., `Event` becomes `@event`).

## Variables and Frames

Every `Frame` maintains two lists:

- **`creates`** -- variables this frame produces. Downstream frames that need this type will depend on this frame.
- **`uses`** -- variables this frame consumes. The engine will find or create the producing frame automatically.

When a frame calls `Create<T>()` or `Create(Type)`, the returned `Variable` has its `Creator` set to that frame and is added to the frame's `creates` list.

## Variable Resolution

During code arrangement, the engine resolves variables in this order:

1. **Locally created variables** -- a variable created by a frame earlier in the chain.
2. **Method arguments** -- parameters declared on the `GeneratedMethod`.
3. **Injected fields** -- fields declared on the `GeneratedType` (constructor-injected dependencies).
4. **Variable sources** -- custom `IVariableSource` implementations registered on the `GenerationRules`.

If a variable cannot be resolved through any of these sources, the code generation will throw an exception describing the missing dependency.

## InjectedField

`InjectedField` is a specialized `Variable` that represents a constructor parameter and corresponding private field on the generated type:

```csharp
var field = new InjectedField(typeof(ILogger));
type.AllInjectedFields.Add(field);
```

This produces a constructor parameter and a `private readonly` field assignment in the generated class. Any frame that needs an `ILogger` variable will automatically resolve to this injected field.
