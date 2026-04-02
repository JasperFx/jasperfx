# Generated Types & Methods

The `GeneratedAssembly`, `GeneratedType`, and `GeneratedMethod` classes form the structural backbone of the JasperFx code generation system. Together they define what classes and methods will be generated, compiled, and optionally persisted.

## GeneratedAssembly

`GeneratedAssembly` is the top-level container. It holds a collection of `GeneratedType` instances and produces a single C# source file when `GenerateCode()` is called.

```csharp
var rules = new GenerationRules("MyApp.Generated");
var assembly = new GeneratedAssembly(rules);
```

Key members:

| Member | Description |
|--------|-------------|
| `Rules` | The `GenerationRules` governing namespace, type load mode, and variable sources. |
| `Namespace` | The C# namespace for all generated types. |
| `GeneratedTypes` | Read-only list of types added to this assembly. |
| `AddType(name, baseType)` | Creates a new `GeneratedType` that inherits from or implements the given type. |
| `GenerateCode()` | Arranges all frames and returns the complete C# source code. |

## GeneratedType

A `GeneratedType` represents a single class to be generated. It can inherit from a base class, implement interfaces, and hold injected fields.

### Inheriting from a Base Class

<!-- snippet: sample_building_generated_type -->
<a id='snippet-sample_building_generated_type'></a>
```cs
public static string BuildGeneratedType()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    // Create a type that inherits from a base class
    var type = assembly.AddType("MyMessageHandler", typeof(MessageHandlerBase));

    // The method defined on the base class is discovered automatically.
    // Retrieve it by name.
    var handleMethod = type.MethodFor("Handle");

    // Add frames to define the method body
    handleMethod.Frames.Code("Console.WriteLine(\"Handling message...\");");
    handleMethod.Frames.Code("return Task.CompletedTask;");

    // Generate all source code for the assembly
    var code = assembly.GenerateCode();

    return code;
}

public abstract class MessageHandlerBase
{
    public abstract Task Handle(Message message);
}

public class Message;
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/GeneratedTypesSamples.cs#L9-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_building_generated_type' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When a type inherits from a base class, the engine:

1. Discovers all overridable methods on the base and adds them as `GeneratedMethod` entries.
2. Reads the base class constructor parameters and registers them as `InjectedField` entries on the generated type.

### Implementing an Interface

<!-- snippet: sample_generated_type_with_interface -->
<a id='snippet-sample_generated_type_with_interface'></a>
```cs
public static string ImplementInterface()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    // Create a type that implements an interface
    var type = assembly.AddType("WidgetValidator", typeof(IValidator));

    var method = type.MethodFor(nameof(IValidator.Validate));
    method.Frames.Code("return {0} != null;", Use.Type<object>());

    return assembly.GenerateCode();
}

public interface IValidator
{
    bool Validate(object input);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/GeneratedTypesSamples.cs#L42-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_generated_type_with_interface' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

All methods declared on the interface are added as `GeneratedMethod` instances that you populate with frames.

### Injected Fields

<!-- snippet: sample_generated_type_injected_fields -->
<a id='snippet-sample_generated_type_injected_fields'></a>
```cs
public static string TypeWithInjectedFields()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("NotificationSender", typeof(NotificationSenderBase));

    // The InjectedField appears as a constructor argument and private field
    var loggerField = new InjectedField(typeof(ILogger));
    type.AllInjectedFields.Add(loggerField);

    var method = type.MethodFor("Send");
    method.Frames.Code("Console.WriteLine(\"Sending notification\");");

    return assembly.GenerateCode();
}

public abstract class NotificationSenderBase
{
    public abstract void Send(string recipient, string body);
}

public interface ILogger
{
    void Log(string message);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/GeneratedTypesSamples.cs#L65-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_generated_type_injected_fields' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An `InjectedField` generates:
- A constructor parameter.
- A `private readonly` field.
- An assignment in the constructor body.

Any frame that needs the field's type will resolve to the injected field automatically.

## GeneratedMethod

A `GeneratedMethod` holds the `Frames` collection that defines the method body. Methods are either discovered from a base class / interface or added manually.

### Discovered Methods

When you call `assembly.AddType("Name", typeof(SomeBase))`, all overridable methods on `SomeBase` become `GeneratedMethod` entries. Retrieve them with:

```csharp
var method = type.MethodFor("Handle");
```

### Custom Methods

<!-- snippet: sample_generated_method_custom -->
<a id='snippet-sample_generated_method_custom'></a>
```cs
public static string AddCustomMethod()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("Calculator", typeof(object));

    // Add a custom void method
    var method = type.AddVoidMethod("PrintSum",
        new Argument(typeof(int), "a"),
        new Argument(typeof(int), "b"));

    method.Frames.Code("Console.WriteLine(a + b);");

    // Add a method that returns a value
    var multiply = type.AddMethodThatReturns<int>("Multiply",
        new Argument(typeof(int), "x"),
        new Argument(typeof(int), "y"));

    multiply.Frames.Code("return x * y;");

    return assembly.GenerateCode();
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/GeneratedTypesSamples.cs#L96-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_generated_method_custom' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AddVoidMethod` and `AddMethodThatReturns<T>` let you define methods that do not come from a base class or interface.

## Frames Collection

The `Frames` property on `GeneratedMethod` is a `FramesCollection`. You interact with it primarily through:

- `Frames.Add(frame)` -- add any `Frame` instance.
- `Frames.Code(format, args)` -- shorthand to add a `CodeFrame` with a format string.

During `GenerateCode()`, the engine arranges frames by resolving variable dependencies, chains them together, and invokes `GenerateCode` on each frame in order.

## GenerationRules

`GenerationRules` controls assembly-wide settings:

| Property | Description |
|----------|-------------|
| `GeneratedNamespace` | Default namespace for generated types. |
| `TypeLoadMode` | `Dynamic`, `Auto`, or `Static`. See [CLI: codegen Command](./cli). |
| `Sources` | Collection of `IVariableSource` implementations for custom variable resolution. |
| `Assemblies` | Referenced assemblies available during compilation. |
