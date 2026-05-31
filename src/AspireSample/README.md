# JasperFx.Aspire sample

A runnable Aspire AppHost that demonstrates `JasperFx.Aspire` — JasperFx CLI verbs surfaced as
clickable command buttons on a resource in the Aspire dashboard.

This sample is intentionally **not** part of `jasperfx.sln` (it pulls the Aspire AppHost SDK +
Postgres hosting), so the core build/CI doesn't carry it. Build and run it on demand.

## Projects

- **AspireSample.Api** — a minimal JasperFx web app. It registers one environment check that opens
  a connection to the Aspire-managed `appdb` Postgres database, and ends with
  `RunJasperFxCommands(args)`. The check only passes if the injected `ConnectionStrings__appdb`
  value reached the process — so a successful `check-env` proves the JasperFx.Aspire env-resolution
  mechanic worked end to end.
- **AspireSample.AppHost** — the Aspire AppHost. Adds a Postgres container + `appdb` database, runs
  the API project, and demonstrates both features:
  - **A1** — `.WithJasperFxCommands(opts => opts.IncludeMutatingCommands = true)` (on-demand buttons).
  - **A2** — `.WithJasperFxStartup(...)` startup gates (`resources setup` then a blocking `check-env`)
    that run to completion before the `api` resource starts.

## Run it (manual dashboard verification)

Requires Docker (for the Postgres container) and the .NET 10 SDK.

```bash
dotnet run --project src/AspireSample/AspireSample.AppHost
```

Open the dashboard URL printed in the console, then on the **api** resource tile:

1. Wait until the resource is **Running** (the command buttons enable then).
2. Click **Check environment** → the child `check-env` runs, connects to the Aspire-managed
   Postgres, streams its output into the resource's **Console logs**, and shows a success toast.
3. Click **Describe** → the app's configuration description streams into the logs.
4. The mutating buttons (**Apply resources**, **Rebuild projections**, **Write generated code**)
   prompt for confirmation before running.

### Exit criteria (from the design doc, A1 step 0)

Clicking **Check environment** runs the verb, the child connects to the Aspire-managed Postgres,
output appears under the resource's console logs, and the toast reflects success/failure.
