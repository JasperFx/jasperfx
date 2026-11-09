# Spike: runtime-async in JasperFx (jasperfx#1038)

Spike results for #1040, which makes the change for real. Two questions:

1. Can `AssemblyGenerator` turn on runtime-async for the code it compiles in memory, through
   `CSharpParseOptions`?
2. Can the shipped JasperFx assemblies turn it on through a csproj flag for `net11.0` only?

The answer to both is **yes, on net11.0 only, and only with Roslyn 5.x**. Measured on 2026-11-09
with SDK `11.0.100-rc.3.26522.148` (runtime `11.0.0-rc.3`) and runtime `10.0.14`, on macOS arm64.

Neither spike project is in `jasperfx.slnx`. `spikes/Directory.Build.props` and
`spikes/Directory.Packages.props` stop the repo-wide `net9.0;net10.0` targets, LangVersion 13 and
central package versions from reaching them.

## How to tell whether it worked

A runtime-async method is emitted with `MethodImplAttributes.Async` (`0x2000`) on the method. It
has no `[AsyncStateMachine]` attribute and no compiler-generated `<Name>d__N` state machine type.
Both spikes check for all three. They do not only check that the code runs, because state-machine
code runs too.

## 1. In-memory compilation (`spikes/RuntimeAsyncSpike`)

The spike compiles handler-shaped code the way `AssemblyGenerator.Generate(string)` does: one
`CSharpSyntaxTree.ParseText`, `CSharpCompilation.Create`, references taken from the running
runtime's assemblies, and `Emit` to a stream loaded into an `AssemblyLoadContext`. The code covers
`Task<T>`, `ValueTask<T>`, `Task`, `await` in `finally` and in `catch`, `await using`, and a
non-async method that returns a `Task`. It then runs the loaded code and checks the result.

```
dotnet run -f net11.0 --project spikes/RuntimeAsyncSpike                          # Roslyn 5.9.0
dotnet run -f net10.0 --project spikes/RuntimeAsyncSpike
dotnet run -f net11.0 --project spikes/RuntimeAsyncSpike -p:RoslynVersion=5.0.0
```

The change is one line:

```csharp
var options = CSharpParseOptions.Default.WithFeatures([new("runtime-async", "on")]);
var syntaxTree = CSharpSyntaxTree.ParseText(code, options);
```

| Runtime | Roslyn | Flag | Result |
|---|---|---|---|
| net11 rc3 | 5.9.0 | off (today) | State machines (4 types). Runs. |
| net11 rc3 | 5.9.0 | `runtime-async=on` | **No state machines. Every async method has `MethodImpl.Async`. Runs, with the correct result.** |
| net11 rc3 | 5.9.0 | on, plus `LanguageVersion.Preview` | Same as above. Preview is **not** needed. |
| net11 rc3 | 5.6.0 | on | Runtime-async. Runs. |
| net11 rc3 | 5.0.0 (RuntimeCompiler's net10.0 pin) | on | Runtime-async. Runs. |
| net11 rc3 | **4.14.0** | on | **The flag is ignored without a warning.** Still emits state machines. |
| net10.0.14 | 5.9.0 | on | **Does not compile.** `SYSLIB5007`: `AsyncHelpers` is "for evaluation purposes only" in net10. |
| net10.0.14 | 5.9.0 | on, with `SYSLIB5007` suppressed | Emits runtime-async IL. **Loading it fails** with `TypeLoadException ... because the format is invalid`. |

The non-async method that returns a `Task` (`PassThrough`) is left alone in every case, which is
expected.

### What this means for #1040 (`AssemblyGenerator`)

- **Gate on net11 at compile time** (`#if NET11_0_OR_GREATER`, or a property set in the net11.0
  build). Do not gate on a runtime probe. On net10 the compiler refuses the code, and if the refusal
  is suppressed the runtime refuses to load the type. A net11.0 build of JasperFx.RuntimeCompiler
  only runs on net11+, so a compile-time gate is enough.
- **Pin Roslyn to 5.x on net11.0 in `Directory.Packages.props`.** Today there is no net11.0
  condition, so net11.0 gets the unconditional `[4.14.0,6.0.0)` range, and NuGet resolves the
  lowest version in a range: 4.14.0. With 4.14.0 the flag does nothing and says nothing. Both 5.0.0
  (the net10.0 pin) and 5.9.0 work, but 5.0.0 predates .NET 11. Track the SDK's Roslyn instead (the
  RC3 SDK ships 5.12.0).
- **Make it opt-in or opt-out (plan decision D4).** `CSharpParseOptions` is per-compilation, so a
  setting on `GenerationRules` or `AssemblyGenerator` can carry the choice.
  `AssemblyGenerator.Generate(string)` (`AssemblyGenerator.cs:198`) is the only place in `src/`
  that parses generated code; `Compile(GeneratedAssembly, ...)` goes through it too.
- `RuntimeFeature.Async` does not exist as a public field in rc3, so it cannot be used as a feature
  probe. Roslyn decides whether runtime-async is available from the referenced corelib
  (`System.Runtime.CompilerServices.AsyncHelpers`, which is not experimental in net11).

## 2. Csproj flag (`spikes/RuntimeAsyncCsprojFlag`)

The same async code is multi-targeted `net10.0;net11.0`, with the feature turned on for net11.0
only. This is the shape that would go in `Directory.Build.props`:

```xml
<PropertyGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net11.0'))">
    <Features>$(Features);runtime-async=on</Features>
</PropertyGroup>
```

```
dotnet run -f net11.0 --project spikes/RuntimeAsyncCsprojFlag
dotnet run -f net10.0 --project spikes/RuntimeAsyncCsprojFlag
dotnet run -f net11.0 --project spikes/RuntimeAsyncCsprojFlag -p:RuntimeAsync=false   # after deleting bin/ and obj/
```

| TFM | Flag | Result |
|---|---|---|
| net11.0 | on | Runtime-async (`MethodImpl.Async`, no `[AsyncStateMachine]`). Runs. |
| net10.0 | condition false | State machines. Runs. The condition does not reach net10.0. |
| net11.0 | off (clean build) | State machines. **The net11 SDK does not turn runtime-async on by default**, with LangVersion 13, 14, 15 or the default. |
| net11.0 | on, with `LangVersion=13` (the repo's setting) | Runtime-async. The repo's LangVersion 13 does **not** have to change. |

### Gotcha: `Features` does not trigger a recompile

Changing `<Features>` and building again does not rebuild the project. The old IL stays in place,
whether the flag was turned on or off. In this spike, turning the flag off after an "on" build
still reported runtime-async until `bin/` and `obj/` were deleted. Anyone comparing on and off,
including in benchmarks, needs a clean build or
`--no-incremental` between the two runs.

## Not covered here

- Wiring the flag into `AssemblyGenerator`, `Directory.Build.props` and the Roslyn pin. That is
  #1040, which also depends on #1039 (adding net11.0).
- Performance. The spike only checks that the IL is correct and runs, not whether it is faster.
- Debugging, stack traces and `Activity`/`AsyncLocal` flow under runtime-async.
- Mixing state-machine and runtime-async code in one call chain is exercised only indirectly:
  generated runtime-async code awaits BCL methods built as state machines, and that works.
