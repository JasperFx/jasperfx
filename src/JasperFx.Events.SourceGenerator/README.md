# JasperFx.Events.SourceGenerator

Roslyn source generator for fast aggregate projections in the [Critter Stack](https://jasperfx.net) event-store family. Produces zero-reflection projection metadata at compile time so Marten / Polecat projection registration doesn't pay the runtime cost of scanning aggregate types.

Used by `JasperFx.Events`'s `SingleStreamProjection<TDoc, TId>` and `MultiStreamProjection<TDoc, TId>` registrations.

## Install

```xml
<PackageReference Include="JasperFx.Events.SourceGenerator"
                  PrivateAssets="all"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

Reference it as an analyzer/source-generator only — there's no runtime API. Generated code is invisible to consumers and shows up alongside your aggregate types under the `JasperFx.Events.Generated` namespace.

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
