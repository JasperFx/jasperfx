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

## Startup gates

The companion to the on-demand buttons: run a JasperFx provisioning verb as a **run-to-completion
resource that finishes _before_ the service starts**, wired via Aspire's `WaitForCompletion`. This is
the canonical "apply schema / pre-generate code before the app boots" pattern, as a one-liner against
the existing project:

```csharp
var db = builder.AddPostgres("pg").AddDatabase("appdb");

builder.AddProject<Projects.Api>("api")
    .WithReference(db)
    .WaitFor(db)
    .WithJasperFxStartup("resources", "setup"); // gate finishes before "api" starts
```

Each gate is a first-class Aspire resource pointing at the **same project** with the verb as args, so
Aspire injects connection strings/environment natively — no callback or child-process trick. The gate
inherits the parent's references (declare them *before* `WithJasperFxStartup`). A gate that exits
non-zero blocks the service from starting (fail fast).

Declare several gates with ordering control (they run sequentially in declaration order unless marked
`Parallel`):

```csharp
api.WithJasperFxStartup(c =>
{
    c.Run("resources", "setup");                        // gate 1
    c.Run("codegen", "write", g => g.Parallel = true);  // runs independently
    c.Check();                                          // check-env, blocking, opt-in
});
```

- `check-env` is only a gate when you opt in (via `Check()` or `WithJasperFxStartup("check-env")`); a
  failed check then blocks startup.
- Gates run in all environments by default. Make one environment-conditional with `RunWhen`, e.g.
  `g.RunWhen = ctx => ctx.IsRunMode` to run it locally but not in a published deployment.

## Requirements

- .NET Aspire 9.2+ (built and tested against Aspire 13.x).
- The target resource must be a project resource (`AddProject<T>`).
