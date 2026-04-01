# Utility Extensions

JasperFx.Core provides a collection of extension methods used throughout the Critter Stack. These are general-purpose helpers available in any .NET project.

## Installation

If you only need the extensions without the full JasperFx framework:

```bash
dotnet add package JasperFx.Core
```

## Categories

### String Extensions

Helpers for case conversion, empty checks, joining, and common string operations.

[Read more](/extensions/string-extensions)

### Enumerable Extensions

Methods for working with collections including `Fill`, `Each`, and `AddRange` variants.

[Read more](/extensions/enumerable-extensions)

### Reflection Extensions

Type inspection helpers for checking interface implementation, generating readable type names, and working with generics.

[Read more](/extensions/reflection-extensions)

## Namespace

All extension methods live in the `JasperFx.Core` or `JasperFx.Core.Reflection` namespaces:

```csharp
using JasperFx.Core;
using JasperFx.Core.Reflection;
```

## Next Steps

- [String Extensions](/extensions/string-extensions)
- [Enumerable Extensions](/extensions/enumerable-extensions)
- [Reflection Extensions](/extensions/reflection-extensions)
