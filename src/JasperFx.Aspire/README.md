# JasperFx.Aspire

Surface a JasperFx application's command-line verbs as **clickable custom commands** on the
resource tile in the .NET Aspire dashboard.

Every JasperFx-bootstrapped app (including Marten, Wolverine, and Polecat apps) already answers a
rich set of CLI verbs because `Program.cs` ends in `RunJasperFxCommands(args)`. `JasperFx.Aspire`
turns those verbs into dashboard buttons: a developer running their Aspire AppHost can click
**Check environment**, **Describe**, **Apply resources**, or **Rebuild projections** directly
against a running service, with the command's output streamed into the resource's console logs.

## Usage

```csharp
// AppHost Program.cs
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands();        // adds the read-only verbs: check-env, describe, codegen preview

builder.Build().Run();
```

Opt in to the mutating verbs (each gated behind a confirmation prompt):

```csharp
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands(opts =>
    {
        opts.IncludeMutatingCommands = true;     // adds: codegen write, resources, projections
        opts.For("projections").ConfirmationMessage =
            "Rebuild projections for 'api'? This reprocesses the event store.";
    });
```

Add a single verb (standard, product-specific, or custom):

```csharp
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommand("resources", "setup");
```

## How it works

The dashboard button callback runs **inside the AppHost process**. To run a verb against the target
app — with the same DI container, configuration, and (critically) the same Aspire-managed
environment (connection strings to Aspire-managed Postgres, etc.) — it spawns a short-lived child:

```
dotnet run --project <api>.csproj --no-build -- <verb> <args>
```

with the resource's resolved environment applied on top of the inherited AppHost environment. The
child's stdout/stderr stream into the resource's dashboard logs, and its exit code maps to a
success/failure toast.

## Safety

- **Read-only verbs** (`check-env`, `describe`, `codegen preview`) are enabled while the resource is
  running, with no confirmation.
- **Mutating verbs** (`codegen write`, `resources`, `projections`) require explicit opt-in
  (`IncludeMutatingCommands = true`) and prompt for confirmation before running.

## Requirements

- .NET Aspire 9.2+ (built and tested against Aspire 13.x).
- The target resource must be a project resource (`AddProject<T>`).
