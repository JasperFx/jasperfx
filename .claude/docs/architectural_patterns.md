# Architectural Patterns - JasperFx

This document describes the architectural patterns and design decisions used throughout the codebase.

## Command Pattern (CLI Framework)

The command-line framework uses a discoverable command pattern with reflection-based registration.

**Key Interfaces:**
- `IJasperFxCommand` - Core command interface: `src/JasperFx/CommandLine/IJasperFxCommand.cs:9`
- `JasperFxCommand<T>` - Synchronous base: `src/JasperFx/CommandLine/JasperFxCommand.cs:6`
- `JasperFxAsyncCommand<T>` - Async base: `src/JasperFx/CommandLine/JasperFxAsyncCommand.cs:6`

**Discovery Mechanism:**
- `CommandFactory` discovers commands via reflection: `src/JasperFx/CommandLine/CommandFactory.cs:20`
- Commands are named by convention (strips "Command" suffix)
- Assemblies marked with `[JasperFxAssembly]` are scanned automatically

**Usage:**
- Input classes define arguments/flags via property attributes
- `UsageGraph` parses and validates input: `src/JasperFx/CommandLine/UsageGraph.cs`

---

## Frame-Based Code Generation

Runtime code generation uses a composable "frame" abstraction for building C# code.

**Core Components:**
- `Frame` - Base unit of code: `src/JasperFx/CodeGeneration/Frames/Frame.cs:11`
- `GeneratedType` - Class builder: `src/JasperFx/CodeGeneration/Model/GeneratedType.cs:15`
- `GeneratedMethod` - Method builder: `src/JasperFx/CodeGeneration/Model/GeneratedMethod.cs:14`
- `Variable` - Variable representation: `src/JasperFx/CodeGeneration/Model/Variable.cs:11`

**Frame Types:**
- `MethodCall` - Method invocations: `src/JasperFx/CodeGeneration/Frames/MethodCall.cs:13`
- `IfBlock` / `ElseBlock` - Conditionals: `src/JasperFx/CodeGeneration/Frames/IfBlock.cs`
- `ConstructorFrame` - Object instantiation: `src/JasperFx/CodeGeneration/Frames/ConstructorFrame.cs:12`
- `AsyncMode` frame wrapping for sync/async code paths

**Pattern:** Frames assemble into a tree, then `SourceWriter` (`src/JasperFx/CodeGeneration/SourceWriter.cs:7`) serializes to C# source code for Roslyn compilation.

---

## Projection Pattern (Event Sourcing)

Event projections use a graph-based model with filtering and lifecycle management.

**Base Classes:**
- `ProjectionBase` - Abstract projection: `src/JasperFx.Events/Projections/ProjectionBase.cs:10`
- `ProjectionGraph` - Manages all projections: `src/JasperFx.Events/Projections/ProjectionGraph.cs:8`
- `EventFilterable` - Base for event filtering: `src/JasperFx.Events/EventFilterable.cs:7`

**Key Interfaces:**
- `IJasperFxProjection<TOperations>` - Apply events to storage: `src/JasperFx.Events/Projections/IJasperFxProjection.cs:8`
- `IProjectionSource` - Projection factory: `src/JasperFx.Events/Projections/IProjectionSource.cs:8`

**Lifecycle Modes (`ProjectionLifecycle`):**
- `Inline` - Synchronous with event append
- `Async` - Background daemon processing
- `Queued` - Queue for later processing

**Filtering:**
- `IncludedEventTypes` / `ExcludedEventTypes` on `EventFilterable`
- Stream-level filtering via `StreamType` and `TenantId`

---

## Service Location Code Generation

IoC integration generates optimized service resolution code at runtime.

**Components:**
- `ServiceFamily` - Groups implementations: `src/JasperFx/CodeGeneration/Services/ServiceFamily.cs:8`
- `ServicePlan` - Resolution strategy: `src/JasperFx/CodeGeneration/Services/ServicePlan.cs`
- `ConstructorPlan` - Dependency analysis: `src/JasperFx/CodeGeneration/Services/ConstructorPlan.cs`
- `InjectedSingleton` - Singleton field generation: `src/JasperFx/CodeGeneration/Services/InjectedSingleton.cs`

**Pattern:** Analyze constructor dependencies → build resolution graph → generate C# code → compile with Roslyn → replace reflection-based DI.

---

## Type Scanning & Filtering

Composable type discovery using fluent filtering.

**Core Types:**
- `TypeRepository` - Type cache and scanning: `src/JasperFx/Core/TypeScanning/TypeRepository.cs`
- `AssemblyFinder` - Assembly discovery: `src/JasperFx/Core/TypeScanning/AssemblyFinder.cs:9`

**Filters (all implement filtering interface):**
- `CanCastToFilter` - Type assignment: `src/JasperFx/Core/Filters/CanCastToFilter.cs`
- `NamespaceFilter` - Namespace matching: `src/JasperFx/Core/Filters/NamespaceFilter.cs`
- `NameSuffixFilter` - Name suffix matching: `src/JasperFx/Core/Filters/NameSuffixFilter.cs`
- Filters compose via `AndFilter`, `OrFilter`

---

## Descriptor Pattern

Configuration metadata via attributes and reflection for documentation and validation.

**Attributes:**
- `[ChildDescription]` - Mark child properties for recursion
- `[DisallowNull]` - Validation constraint
- `[Description]` - Human-readable descriptions

**Used For:**
- CLI help generation
- Configuration validation
- Runtime introspection of framework objects

**Location:** `src/JasperFx/Descriptors/`

---

## Async Options Pattern

Shared configuration for background processing across projections and daemon.

**Class:** `AsyncOptions` at `src/JasperFx.Events/AsyncOptions.cs:9`

**Properties:**
- `BatchSize` - Events per batch
- `MaximumHopperSize` - Queue capacity
- `EnableRetries` - Retry policy via Polly
- `TeardownDataOnRebuild` - Blue/green deployment support

---

## Multi-Tenancy Support

Tenant isolation built into core abstractions.

**Location:** `src/JasperFx/MultiTenancy/`

**Pattern:**
- Tenant context propagated through operations
- Tenant-scoped storage and projections
- Integration with event slicing for tenant isolation

---

## Variable Dependencies

Code generation tracks variable dependencies for proper ordering.

**Mechanism:**
- `Variable.Dependencies` tracks what other variables are needed
- `Frame.CreateVariable()` registers new variables
- `MethodCall.TrySetArgument()` resolves dependencies
- Topological sort ensures correct initialization order

---

## Extension Methods Organization

Core utilities are organized as extension methods in `src/JasperFx/Core/`:
- `StringExtensions.cs` - String manipulation
- `TaskExtensions.cs` - Async helpers
- `DictionaryExtensions.cs` - Dictionary operations
- `EnumerableExtensions.cs` - LINQ extensions
- `TypeExtensions.cs` - Reflection helpers
