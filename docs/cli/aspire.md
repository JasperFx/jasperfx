# Aspire Dashboard Integration

The `JasperFx.Aspire` package surfaces a JasperFx application's command-line verbs — `check-env`,
`describe`, `codegen`, `resources`, `projections` — as **clickable custom commands** on the
resource tile in the [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) dashboard.

Every JasperFx-bootstrapped application already answers these verbs because `Program.cs` ends in
`RunJasperFxCommands(args)` (see [Setup & Integration](/cli/)). Instead of dropping to a terminal to
run `check-env`, rebuild projections, or apply schema changes, a developer running their Aspire
AppHost can click a button on the running service and watch the output stream into the dashboard.

Because Marten, Wolverine, and Polecat all build on the same JasperFx command infrastructure, a
single package lights this up for the entire Critter Stack.

The package offers two complementary features:

- **On-demand commands** — `WithJasperFxCommands()` adds buttons you click against the *running*
  service.
- **Startup gates** — `WithJasperFxStartup()` runs provisioning verbs to completion *before* the
  service starts (see [Startup gates](#startup-gates) below).

## Installation

Add the package to your Aspire **AppHost** project (not the service itself):

```bash
dotnet add package JasperFx.Aspire
```

`JasperFx.Aspire` targets .NET 10 and requires .NET Aspire 9.2 or later (built and tested against
Aspire 13.x).

## Basic usage

Call `WithJasperFxCommands()` on a project resource in your AppHost:

```cs
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands();

builder.Build().Run();
```

By default this adds the **read-only** verbs, enabled while the resource is running, with no
confirmation prompt:

| Button                 | Verb              | What it does                                              |
|------------------------|-------------------|-----------------------------------------------------------|
| **Check environment**  | `check-env`       | Runs all of the application's environment checks.         |
| **Describe**           | `describe`        | Writes a description of the app's configuration to the logs. |
| **Preview generated code** | `codegen preview` | Previews the code JasperFx would generate at runtime.  |

## Mutating verbs

The verbs that change state are opt-in and each prompt for confirmation before running. Enable them
with `IncludeMutatingCommands`:

```cs
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands(opts =>
    {
        opts.IncludeMutatingCommands = true; // adds: Apply resources, Rebuild projections, Write generated code

        // Tweak the presentation of any verb
        opts.For("projections").ConfirmationMessage =
            "Rebuild projections for 'api'? This reprocesses the event store.";
    });
```

| Button                 | Verb               | What it does                                                       |
|------------------------|--------------------|--------------------------------------------------------------------|
| **Apply resources**    | `resources setup`  | Creates/updates databases, schema, queues, and other infrastructure. |
| **Rebuild projections**| `projections rebuild` | Rebuilds all asynchronous projections from the event store.     |
| **Write generated code** | `codegen write`  | Generates the runtime code ahead of time and writes it to disk.    |

`JasperFxCommandOptions` also exposes `IncludeVerbs` (an explicit allow-list that replaces the
defaults) and `ExcludeVerbs` (to drop individual verbs from the selection).

## Adding a single command

Use `WithJasperFxCommand` to add one verb — a standard verb, a product-specific verb (e.g. Marten's
`projections`), or your own [custom command](/cli/writing-commands) — with optional fixed arguments:

```cs
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommand("resources", "setup")
    .WithJasperFxCommand("storage", "rebuild", reg =>
    {
        reg.DisplayName = "Rebuild storage";
        reg.IconName = "DatabaseArrowUp";
    });
```

Unknown verbs are treated as mutating (safe-by-default) and get a confirmation prompt unless you
override it.

## Dynamic command discovery

By default the buttons come from a curated list of the standard JasperFx verbs. To instead render a
button for **every** verb the target actually exposes — including product-specific commands (Marten's
`projections`, etc.) and your own [custom commands](/cli/writing-commands) — opt into discovery:

```cs
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands(opts => opts.DiscoverCommands = true);
```

At AppHost build time this runs `help --json` against the already-built project to read its command
catalog, then renders one button per verb (known verbs keep their curated icon/confirmation; unknown
verbs are treated as mutating). The same `IncludeMutatingCommands` / `IncludeVerbs` / `ExcludeVerbs`
gating applies, and `run`/`help` are never shown.

Discovery is **best-effort**: if the project isn't built yet, the call times out, or the output can't
be parsed, it silently falls back to the curated catalog. Because it uses `--no-build`, build the
target first (a normal `dotnet build` of your solution) for discovery to succeed.

> `help --json` is a general-purpose, machine-readable command catalog — it lists each verb's name and
> description as JSON and runs without starting the host (no database/broker connections), so it's
> cheap to call from tooling.

## How it works

The dashboard command callback runs **inside the AppHost process**, not the target application. To
run a verb against the service — with its own DI container, configuration, and (critically) the same
Aspire-managed environment, including connection strings to Aspire-managed dependencies — the
callback spawns a short-lived child process of the same application:

```bash
dotnet run --project <api>.csproj --no-build -- <verb> <args>
```

The child inherits the AppHost's environment, and on top of that the resource's *resolved*
environment is applied — evaluated from the resource's Aspire environment annotations (the same
values Aspire injects into the running process, such as `ConnectionStrings__*`). The child's
`stdout`/`stderr` stream into the resource's **Console logs** in the dashboard, and its exit code
maps to a success or failure toast.

```mermaid
sequenceDiagram
    participant Dev as Developer
    participant Dash as Aspire Dashboard
    participant Host as AppHost (JasperFx.Aspire)
    participant Child as dotnet run -- check-env

    Dev->>Dash: Click "Check environment"
    Dash->>Host: executeCommand(ctx)
    Host->>Host: Resolve project path + resource environment
    Host->>Child: spawn with resolved env
    Child-->>Host: stdout / stderr
    Host-->>Dash: stream into resource logs
    Child-->>Host: exit code
    Host-->>Dash: success / failure toast
```

This reuses the exact CLI path the framework already supports — the target application needs no
changes beyond the `RunJasperFxCommands(args)` it already calls.

## Safety

- **Read-only verbs** (`check-env`, `describe`, `codegen preview`) are enabled while the resource is
  running, with no confirmation.
- **Mutating verbs** (`codegen write`, `resources`, `projections`) require explicit opt-in via
  `IncludeMutatingCommands` and prompt for confirmation before running.
- The buttons are disabled unless the resource is running, so the child process always has something
  to run against. Override per verb with `JasperFxCommandRegistration.UpdateState`.

## Startup gates

The on-demand buttons above run against a service that is *already running*. The complementary need
is to run a provisioning verb **before** the service starts — apply database schema / event store /
transports, or pre-generate runtime code so there's no first-request codegen latency. .NET Aspire
models this with run-to-completion resources and `WaitForCompletion` (the canonical "run migrations
before the app" pattern), and `WithJasperFxStartup` makes it a one-liner against the existing project:

```cs
var db = builder.AddPostgres("pg").AddDatabase("appdb");

builder.AddProject<Projects.Api>("api")
    .WithReference(db)
    .WaitFor(db)
    .WithJasperFxStartup("resources", "setup"); // runs to completion before "api" starts
```

Each gate is a **first-class Aspire resource** pointing at the same project with the verb as
arguments. Because it is a real resource (not an AppHost callback), Aspire injects connection strings
and environment into it natively — there is no child-process spawn or environment trick here. The gate
inherits the parent's references, so you declare `WithReference`/`WaitFor` **once on the service,
before** `WithJasperFxStartup`.

When `arguments` is omitted, the provisioning verbs default sensibly: `resources` → `setup`,
`codegen` → `write`.

### Fail fast

A gate that exits non-zero leaves the service **blocked** and the failure visible in the dashboard
with the gate's streamed logs — you never start a service against un-provisioned infrastructure. This
is the default and the whole point.

### Several gates and ordering

Use the fluent form to declare multiple gates. They run **sequentially in declaration order** (each
waiting for the previous) unless a gate opts into `Parallel`:

```cs
api.WithJasperFxStartup(c =>
{
    c.Run("resources", "setup");                       // gate 1
    c.Run("codegen", "write", g => g.Parallel = true); // independent — runs concurrently
    c.Check();                                         // check-env, blocking (opt-in)
});
```

### `check-env` as an opt-in gate

`check-env` is **not** a startup gate unless you ask for it — many teams treat environment checks as
advisory and silently blocking startup would be surprising. Opt in with `c.Check()` (fluent form) or
`WithJasperFxStartup("check-env")`; a failed check then blocks startup like any other gate. Make a gate
advisory (runs but never blocks) with `BlockOnFailure = false`.

### Published / deploy-time behavior

Gates run in all environments by default, since "provision/migrate on deploy" is a common, valid use.
Because migrating in production is a deliberate policy for some teams, make a gate
environment-conditional with `RunWhen`:

```cs
api.WithJasperFxStartup("resources", "setup",
    gate: g => g.RunWhen = ctx => ctx.IsRunMode); // local only, not in a published deployment
```

> Each gate runs the target project with verb arguments, which relies on the standard JasperFx
> bootstrap (`RunJasperFxCommands(args)`) so the process executes the verb and exits instead of
> starting the long-running host. This is already the default for Marten/Wolverine/Polecat apps.

## Requirements

- The target must be a **project resource** (`AddProject<T>`) — the integration locates the project
  to launch from the resource's project metadata.
- .NET Aspire 9.2+ (the `CommandOptions` overload of `WithCommand`).
