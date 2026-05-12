# JasperFx.SourceGenerator

Roslyn source generator for [JasperFx](https://jasperfx.net) `OptionsDescription` types. Generates `IDescribeMyself.ToDescription()` implementations at compile time, eliminating runtime reflection on options classes that surface in diagnostic snapshots (CritterWatch, etc.).

## Install

```xml
<PackageReference Include="JasperFx.SourceGenerator"
                  PrivateAssets="all"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

Reference it as an analyzer/source-generator only — there's no runtime API. Apply the `[GenerateDescription]` attribute to a class that implements `IDescribeMyself` and a `ToDescription()` method is generated.

## Documentation

Full docs at [https://jasperfx.net](https://jasperfx.net).

Repo: [github.com/JasperFx/jasperfx](https://github.com/JasperFx/jasperfx).
