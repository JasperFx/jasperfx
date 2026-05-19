# Pre-generating Codegen in Docker

Any host that opts into the JasperFx command-line execution surface (`return await app.RunJasperFxCommands(args);`) gets access to the `codegen` verb family documented on the [CLI: codegen Command](./cli) page. The `write` subcommand inspects the host's registered Critter-Stack tools and serializes their generated code under `Internal/Generated/` so a production image can ship the artifacts pre-built rather than pay the runtime-codegen cost on cold start.

This page covers the Dockerfile pattern for that posture.

## When to use this

`codegen write` is the half-step between pure runtime codegen (the default, best for development) and full AOT publishing (the strongest production posture):

| Posture | Runtime codegen on startup? | Best for |
|---------|---|----------|
| `TypeLoadMode.Dynamic` | Yes — every cold start | Local development |
| `TypeLoadMode.Auto` + pre-built artifacts | Falls back to runtime if a type is missing | Staging |
| `TypeLoadMode.Static` + pre-built artifacts | No — fails if a type is missing | Production cold-start sensitivity |
| AOT publish | No — Roslyn isn't present at runtime at all | Trimming / single-file / native AOT |

The Dockerfile below produces a `Static` or `Auto` image: it runs `codegen write` during the build stage, ships the resulting `Internal/Generated/*.cs` files inside the published assembly, and the production container starts without ever loading Roslyn. Consumers that already use the [AOT publishing posture](./aot) don't need this — `codegen write` is the half-step for the still-JIT case that wants production cold-start without giving up runtime flexibility entirely.

## The Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["Application/Application.csproj", "Application/"]

# you might need more projects depending on your set-up
# COPY ["Shared/Shared.csproj", "Shared/"]

COPY . .
WORKDIR "/src/Application"

RUN dotnet run -- codegen write
RUN dotnet publish "Application.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
ENV DOTNET_RUNNING_IN_CONTAINER=1
ENV DOTNET_NOLOGO=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
RUN addgroup -g 1001 -S nonroot && adduser -u 1001 -S nonroot -G nonroot
RUN mkdir /app
RUN chown nonroot:nonroot /app
WORKDIR /app
COPY --chown=nonroot:nonroot --from=build /app/publish .

FROM runtime
EXPOSE 5000
USER nonroot
ENTRYPOINT ["dotnet", "Application.dll"]
```

The `RUN dotnet run -- codegen write` step is the build-stage hook that converts a runtime-codegen host into a pre-built one. The subsequent `dotnet publish` step picks up the freshly written `Internal/Generated/*.cs` files and compiles them into `Application.dll`.

The base-image tags above target .NET 10 LTS, current as of mid-2026. Bump to whatever LTS is current when you adopt the pattern — `10.0-alpine` is just a placeholder.

## Constraint: the build-stage host must construct without external resources

`dotnet run -- codegen write` builds the host's DI container so the registered Critter-Stack tools can be introspected. It does **not** call `host.RunAsync()` and therefore does not invoke `IHostedService.StartAsync` — but any work that runs *eagerly* during DI registration or `IHost` construction will still run inside Docker, where the database, message broker, or other external resources are usually unreachable.

Typical failure modes:

- An `AddNpgsqlDataSource(connectionString)` registration that probes the connection eagerly.
- A `services.AddSingleton<IFoo>(sp => new Foo(connectionString))` factory that connects in its constructor.
- A configuration provider that resolves a secret from a remote vault during `builder.Build()`.

The cleanest workarounds:

1. **Make resource access lazy.** Defer the actual connection / probe to the first call, not to construction. This is good hygiene independent of Docker codegen.
2. **Skip resource-heavy registration when running a CLI verb.** Guard the registration on the args:

    ```csharp
    var builder = WebApplication.CreateBuilder(args);

    if (!args.Contains("codegen"))
    {
        // Eager resource-touching registrations gated here so `codegen write`
        // can build the DI container without a live database / broker.
        builder.Services.AddSomeResourceThatProbesAtStartup();
    }

    var app = builder.Build();
    return await app.RunJasperFxCommands(args);
    ```

3. **Move resource-touching logic into `IHostedService`.** Hosted services don't run during `codegen write`, so any startup probes inside `StartAsync` are naturally bypassed.

## Marten-only deployments: this pattern is a no-op

Marten 9.0 [removed its runtime code-generation pipeline entirely](https://github.com/JasperFx/marten/pull/4461), so a host that registers only Marten has no generated code to write — `dotnet run -- codegen write` will exit cleanly with nothing to do. The Dockerfile pattern still applies once you register Wolverine or another JasperFx-family tool that's backed by runtime codegen; for a Marten-only host, you can drop the `RUN dotnet run -- codegen write` line.

## See also

- [CLI: codegen Command](./cli) — the verb family this build-stage step invokes, plus the `TypeLoadMode` switches you'll typically pair with pre-generated artifacts.
- [Publishing AOT](./aot) — the stronger posture for trim / single-file / native-AOT deployments where Roslyn isn't present at runtime at all.
