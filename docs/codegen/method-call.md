# MethodCall

`MethodCall` is the most commonly used `Frame` in the JasperFx code generation system. It generates a call to an existing .NET method -- either static or instance -- and automatically wires up arguments and return values through the variable resolution system.

## Basic Usage

<!-- snippet: sample_method_call_basic -->
<a id='snippet-sample_method_call_basic'></a>
```cs
public static void BasicMethodCall()
{
    // Create a MethodCall by type and method name
    var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrder));

    // The ReturnVariable is automatically created from the method's return type
    Console.WriteLine(call.ReturnVariable!.VariableType); // typeof(OrderResult)

    // Arguments array matches the method's parameters
    Console.WriteLine(call.Arguments.Length); // matches parameter count
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/MethodCallSamples.cs#L9-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_method_call_basic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When you create a `MethodCall`, the frame inspects the target method via reflection and:

1. Sets `IsAsync` based on whether the method returns `Task` or `ValueTask`.
2. Creates a `ReturnVariable` if the method has a non-void return type (unwrapping `Task<T>` to `T`).
3. Allocates an `Arguments` array matching the method's parameters.

## Async Methods

<!-- snippet: sample_method_call_async -->
<a id='snippet-sample_method_call_async'></a>
```cs
public static void AsyncMethodCall()
{
    // Async methods are automatically detected
    var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrderAsync));

    // The frame knows it is async
    Console.WriteLine(call.IsAsync); // true

    // ReturnType unwraps Task<T> to T
    Console.WriteLine(call.ReturnVariable!.VariableType); // typeof(OrderResult)
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/MethodCallSamples.cs#L25-L39' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_method_call_async' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Async methods are detected automatically. The generated code will emit `await` before the method call, and the enclosing generated method will be marked as `async`.

## ReturnAction

The `ReturnAction` property controls how the method call's return value is rendered in the generated code:

<!-- snippet: sample_method_call_return_action -->
<a id='snippet-sample_method_call_return_action'></a>
```cs
public static void ReturnActions()
{
    var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrder));

    // Initialize: generates "var orderResult = ProcessOrder(...);"
    call.ReturnAction = ReturnAction.Initialize;

    // Assign: generates "orderResult = ProcessOrder(...);"
    call.ReturnAction = ReturnAction.Assign;

    // Return: generates "return ProcessOrder(...);"
    call.ReturnAction = ReturnAction.Return;
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/MethodCallSamples.cs#L41-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_method_call_return_action' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

| Value | Generated Code |
|-------|---------------|
| `Initialize` | `var result = Method(...);` |
| `Assign` | `result = Method(...);` |
| `Return` | `return Method(...);` |

The default is `Initialize`.

## DisposalMode

When a method returns an `IDisposable` or `IAsyncDisposable`, you can wrap it in a using block:

<!-- snippet: sample_method_call_disposal -->
<a id='snippet-sample_method_call_disposal'></a>
```cs
public static void disposal_mode_example()
{
    var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.CreateConnection));

    // Wrap the return value in a using block
    call.DisposalMode = DisposalMode.UsingBlock;
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/MethodCallSamples.cs#L59-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_method_call_disposal' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

| Value | Behavior |
|-------|----------|
| `None` | No disposal handling (default). |
| `UsingBlock` | Wraps the call in a `using` statement so the return value is disposed at the end of scope. |

## Arguments

The `Arguments` array on a `MethodCall` holds one `Variable` per method parameter. By default, arguments are left `null` and resolved automatically by the variable system during code arrangement. You can also pin a specific variable to an argument slot:

```csharp
var call = new MethodCall(typeof(Service), nameof(Service.DoWork));
call.Arguments[0] = someSpecificVariable;
```

This is useful when you need to override the default resolution for a particular parameter.

## Using MethodCall in a Generated Type

<!-- snippet: sample_method_call_in_generated_type -->
<a id='snippet-sample_method_call_in_generated_type'></a>
```cs
public static string UseMethodCallInGeneratedType()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("OrderHandler", typeof(IOrderHandler));
    var method = type.MethodFor(nameof(IOrderHandler.Handle));

    // Add a MethodCall frame
    var call = new MethodCall(typeof(OrderProcessor), nameof(OrderProcessor.ProcessOrder));
    method.Frames.Add(call);

    return assembly.GenerateCode();
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/MethodCallSamples.cs#L71-L88' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_method_call_in_generated_type' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Instance vs. Static Methods

- **Static methods** -- the generated code calls the method directly on the type (e.g., `OrderProcessor.ProcessOrder(...)`).
- **Instance methods** -- the code generation engine resolves a variable of the handler type and calls the method on that instance (e.g., `orderProcessor.ProcessOrder(...)`). The instance variable is resolved through the same mechanism as any other variable (injected field, method argument, or variable source).

## HandlerType

The `HandlerType` property identifies the type that owns the method. For instance methods, a variable of this type will be resolved or created so the call can be made.

## Tuple Return Values

If the target method returns a `ValueTuple`, `MethodCall` automatically deconstructs the tuple into individual variables:

```csharp
// If the method returns (string Name, int Age)
// the generated code will be:
// var (name, age) = Method(...);
```

Each element becomes a separate `Variable` that downstream frames can reference.
