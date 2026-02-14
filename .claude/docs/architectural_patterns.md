# Architectural Patterns - JasperFx

## 1. Convention-Over-Configuration via Plugin Architecture

Services and types are discovered automatically through scanning conventions rather than explicit registration.

**Key types:**
- `IRegistrationConvention` - Pluggable scanning strategy: `src/JasperFx/Core/IoC/IRegistrationConvention.cs:11`
- `AssemblyScanner` - Main orchestrator with include/exclude filtering: `src/JasperFx/Core/IoC/AssemblyScanner.cs:27`
- `DefaultConventionScanner` - Default first-interface convention: `src/JasperFx/Core/IoC/DefaultConventionScanner.cs:7`
- `FirstInterfaceConvention` - Maps `MyService` to `IMyService`: `src/JasperFx/Core/IoC/FirstInterfaceConvention.cs`

**How it works:** Assemblies marked with `[JasperFxAssembly]` are scanned. `IRegistrationConvention` implementations evaluate each discovered type and register matching services. Multiple conventions compose together, reducing boilerplate registration code.

---

## 2. Frame-Based Code Generation

Runtime C# code generation uses a composable tree of `Frame` and `Variable` objects that serialize to Roslyn-compilable source.

**Core abstractions:**
- `Frame` - Base unit of generated code: `src/JasperFx/CodeGeneration/Frames/Frame.cs:11`
- `Variable` - Represents a value with dependency tracking: `src/JasperFx/CodeGeneration/Model/Variable.cs:11`
- `GeneratedType` - Builds a complete class: `src/JasperFx/CodeGeneration/Model/GeneratedType.cs:15`
- `GeneratedMethod` - Builds a method within a type: `src/JasperFx/CodeGeneration/Model/GeneratedMethod.cs:14`
- `IMethodVariables` - Variable resolution interface: `src/JasperFx/CodeGeneration/Model/IMethodVariables.cs:10`

**Concrete frame types:**
- `MethodCall` - Method invocations: `src/JasperFx/CodeGeneration/Frames/MethodCall.cs:13`
- `ConstructorFrame` - Object instantiation: `src/JasperFx/CodeGeneration/Frames/ConstructorFrame.cs:12`
- `CompositeFrame` - Composes multiple frames: `src/JasperFx/CodeGeneration/Frames/CompositeFrame.cs:5`
- `IfBlock` / `ElseBlock` - Conditional code: `src/JasperFx/CodeGeneration/Frames/IfBlock.cs`

**Flow:** Frames assemble into a tree -> `SourceWriter` (`src/JasperFx/CodeGeneration/SourceWriter.cs:7`) serializes to C# -> Roslyn compiles to IL. Variables track dependencies via `Variable.Dependencies` and topological sort ensures correct initialization order.

---

## 3. Service Resolution Code Generation

IoC integration generates optimized service resolution code at runtime, replacing reflection-based DI.

**Components:**
- `ServiceFamily` - Groups implementations by service type: `src/JasperFx/CodeGeneration/Services/ServiceFamily.cs:8`
- `ServicePlan` - Resolution strategy for a service: `src/JasperFx/CodeGeneration/Services/ServicePlan.cs`
- `ConstructorPlan` - Analyzes constructor dependencies: `src/JasperFx/CodeGeneration/Services/ConstructorPlan.cs`
- `InjectedSingleton` - Generates singleton field injection: `src/JasperFx/CodeGeneration/Services/InjectedSingleton.cs`

**Flow:** Analyze constructor dependencies -> build resolution graph -> generate C# code -> compile with Roslyn -> replace reflection-based resolution with compiled code.

---

## 4. Event Projection Pattern

Projections use a graph-based model with filtering, lifecycle management, and versioning for blue/green deployments.

**Base classes:**
- `ProjectionBase` - Abstract projection with lifecycle/versioning: `src/JasperFx.Events/Projections/ProjectionBase.cs:10`
- `EventFilterable` - Composable event type filtering: `src/JasperFx.Events/EventFilterable.cs:7`
- `ProjectionGraph` - Manages all registered projections: `src/JasperFx.Events/Projections/ProjectionGraph.cs:8`

**Key interfaces:**
- `IJasperFxProjection<TOperations>` - Applies events to storage: `src/JasperFx.Events/Projections/IJasperFxProjection.cs:8`
- `IProjectionSource` - Projection factory: `src/JasperFx.Events/Projections/IProjectionSource.cs:8`

**Lifecycle modes** (`ProjectionLifecycle`):
- `Inline` - Synchronous with event append
- `Async` - Background daemon processing
- `Queued` - Queue for later processing

**Filtering:** `IncludedEventTypes` / `ExcludedEventTypes` on `EventFilterable`, plus stream-level filtering via `StreamType` and `TenantId`.

**Versioning:** `[ProjectionVersion]` attribute (`src/JasperFx.Events/Projections/ProjectionVersionAttribute.cs:9`) enables blue/green deployment by tracking projection schema versions.

---

## 5. Command Pattern (CLI Framework)

CLI commands are modeled as discoverable objects with reflection-based input binding and auto-generated help.

**Key types:**
- `IJasperFxCommand` - Core command interface: `src/JasperFx/CommandLine/IJasperFxCommand.cs:5`
- `JasperFxCommand<T>` - Synchronous base: `src/JasperFx/CommandLine/JasperFxCommand.cs:6`
- `JasperFxAsyncCommand<T>` - Async base: `src/JasperFx/CommandLine/JasperFxAsyncCommand.cs:6`
- `CommandFactory` - Discovers commands via reflection: `src/JasperFx/CommandLine/CommandFactory.cs:20`
- `UsageGraph` - Parses and validates input: `src/JasperFx/CommandLine/UsageGraph.cs`

**Conventions:** Commands are named by stripping the "Command" suffix (e.g., `CheckCommand` -> `check`). Input classes define arguments/flags via property attributes like `[Description]`, `[FlagAlias]`, `[IgnoreOnCommandLine]`.

---

## 6. Composable Type Filtering

Type discovery uses composable include/exclude predicates that combine for powerful filtering rules.

**Types:**
- `IFilter<T>` - Core match interface: `src/JasperFx/Core/Filters/IFilter.cs:3`
- `CompositeFilter<T>` - Combines include/exclude rules: `src/JasperFx/Core/Filters/CompositeFilter.cs:5`
- `LambdaFilter<T>` - Lambda-based filtering: `src/JasperFx/Core/Filters/LambdaFilter.cs`
- `CanCastToFilter` - Type assignment checks: `src/JasperFx/Core/Filters/CanCastToFilter.cs`
- `NamespaceFilter` - Namespace matching: `src/JasperFx/Core/Filters/NamespaceFilter.cs`
- `HasAttributeFilter` - Attribute-based filtering: `src/JasperFx/Core/TypeScanning/HasAttributeFilter.cs:6`

**Usage:** `AssemblyScanner` composes filters via fluent `.Include()` / `.Exclude()` methods. Filters combine via `AndFilter` and `OrFilter` for complex rules (e.g., "include interfaces except those with `[JasperFxIgnore]`").

---

## 7. Attribute-Driven Configuration

Metadata is applied via attributes to control behavior during scanning, CLI binding, and code generation.

**CLI attributes:**
- `[Description]` - Help text: `src/JasperFx/CommandLine/DescriptionAttribute.cs:6`
- `[FlagAlias]` - Custom flag names: `src/JasperFx/CommandLine/FlagAliasAttribute.cs:7`
- `[IgnoreOnCommandLine]` - Exclude from binding: `src/JasperFx/CommandLine/IgnoreOnCommandLineAttribute.cs:7`
- `[InjectService]` - Service injection marker: `src/JasperFx/CommandLine/InjectServiceAttribute.cs:6`

**Scanning attributes:**
- `[JasperFxIgnore]` - Global ignore marker: `src/JasperFx/Core/JasperFxIgnoreAttribute.cs:6`
- `[IgnoreAssembly]` - Skip assembly scanning: `src/JasperFx/Core/TypeScanning/IgnoreAssemblyAttribute.cs:3`

**Descriptor attributes:**
- `[ChildDescription]` - Nested descriptor: `src/JasperFx/Descriptors/ChildDescriptionAttribute.cs:7`
- `[ProjectionVersion]` - Projection versioning: `src/JasperFx.Events/Projections/ProjectionVersionAttribute.cs:9`

---

## 8. Template Method Pattern via Abstract Base Classes

Base classes define a skeleton with virtual methods that subclasses override for specific behavior.

**Examples:**
- `Frame` - Abstract `GenerateCode()` method: `src/JasperFx/CodeGeneration/Frames/Frame.cs:11`
- `ProjectionBase` - Virtual `AssembleAndAssertValidity()`: `src/JasperFx.Events/Projections/ProjectionBase.cs:75`
- `StatefulResourceBase` - Resource lifecycle with empty defaults: `src/JasperFx/Resources/StatefulResourceBase.cs:9`
- `Fragment` - Abstract markup fragment: `src/JasperFx/Descriptors/Fragment.cs:7`
- `TokenHandlerBase` - Token parsing template: `src/JasperFx/CommandLine/Parsing/TokenHandlerBase.cs`

**Rationale:** Provides sensible defaults while enabling downstream projects (Marten, Wolverine) to customize behavior without modifying core code.

---

## 9. Options/Configuration with Fluent Builders

Configuration objects expose properties and fluent methods, often using internal strategy patterns.

**Examples:**
- `AsyncOptions` - Rich config with strategy methods like `SubscribeFromPresent()`: `src/JasperFx.Events/Projections/AsyncOptions.cs:18`
- `ErrorHandlingOptions` - Simple boolean flags: `src/JasperFx.Events/Daemon/ErrorHandlingOptions.cs:3`
- `JasperFxOptions` - Root options with profiles: `src/JasperFx/JasperFxOptions.cs:17`

**Pattern:** Fluent methods return `this` for chaining. `AsyncOptions` internally uses a strategy pattern for different subscription start positions (from sequence, from time, from present).

---

## 10. Exception Transformation Pipeline

Exceptions are intercepted and optionally transformed to more contextual exceptions using a chain of responsibility.

**Types:**
- `IExceptionTransform` - Core transform interface: `src/JasperFx/Core/Exceptions/IExceptionTransform.cs:11`
- Generic transform with filtering rules: `src/JasperFx/Core/Exceptions/IExceptionTransform.cs:92`
- `ApplyEventException` - Wraps event errors with sequence/ID context: `src/JasperFx.Events/Daemon/ApplyEventException.cs:3`
- `InvalidProjectionException` - Detailed projection error context: `src/JasperFx.Events/Projections/InvalidProjectionException.cs:10`

**Rationale:** Provides rich, contextual error messages for debugging in distributed systems. Multiple transform rules evaluate in pipeline order.

---

## 11. Extension Method Organization

Core utilities are organized as static extension method classes in `src/JasperFx/Core/`:

- `StringExtensions.cs` - String manipulation (Elid, CombineToPath, etc.)
- `EnumerableExtensions.cs` - LINQ extensions (Fill, Each, TopologicalSort)
- `DictionaryExtensions.cs` - Dictionary operations
- `TaskExtensions.cs` - Async helpers
- `TypeExtensions.cs` - Reflection helpers (in `Core/IoC/`)
- `ReflectionExtensions.cs` - Attribute querying, generic helpers (in `Core/Reflection/`)
- `TypeNameExtensions.cs` - Naming conventions (in `Core/Reflection/`)

---

## Naming Conventions

| Pattern | Convention | Example |
|---------|-----------|---------|
| Interfaces | `I` prefix | `IJasperFxCommand`, `IFilter<T>` |
| Attributes | `Attribute` suffix | `DescriptionAttribute`, `JasperFxIgnoreAttribute` |
| Options | `Options` suffix | `AsyncOptions`, `ErrorHandlingOptions` |
| Base classes | `Base` suffix | `ProjectionBase`, `StatefulResourceBase` |
| Extensions | `Extensions` suffix | `ReflectionExtensions`, `EnumerableExtensions` |
| Records | Immutable data | `record DatabaseId(string Server, string Name)` |
