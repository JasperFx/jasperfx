# Spike: System.Text.Json 11 union serialization (jasperfx#1037)

How System.Text.Json in .NET 11 serializes C# 15 unions, and what that means for union events in
JasperFx.Events (jasperfx#1053) and for the union type-test SQL in Weasel (weasel#744).

The runtime behaviour below is pinned by the tests in this folder. Three findings are
compile-time and come from builds, not tests: CS0592 for `[JsonPolymorphic]` on a union, CS0518 /
CS0656 when a net10 target uses the `union` keyword, and the SYSLIB1227 warning, which shows up
when you build this project. The SQL column of the "what is stored" table, and the
recommendations at the end, are advice for the downstream issues and were not measured. To
rerun the tests, use a .NET 11 SDK:

```bash
dotnet test spikes/StjUnionSpike
```

## Scope and caveats

- **Measured on .NET 11 RC3** (SDK `11.0.100-rc.3.26522.148`), not RC2 as the issue title says.
  RC3 is what is installed. The API could still change before GA, and RC2 may differ. Rerun the
  suite on each new SDK: the tests assert exact behaviour, so a change shows up as a red test.
- **net11.0 only, not in `jasperfx.slnx`, not built by CI.** CI installs the 9.0 and 10.0 SDKs.
  The `union` keyword will not compile against net10 at all: `CS0518 Predefined type
  'System.Runtime.CompilerServices.IUnion' is not defined or imported`, plus `CS0656` for
  `UnionAttribute`. Any union support in JasperFx has to sit behind `#if NET11_0_OR_GREATER`.
- The project references `src/JasperFx.Events` only to show today's event naming. It changes
  nothing under `src/`.

## The API surface (RC3)

| Where | Type | What it is |
|---|---|---|
| `System.Runtime` | `System.Runtime.CompilerServices.UnionAttribute` | Marks a union type, whether written with the keyword or by hand |
| `System.Runtime` | `System.Runtime.CompilerServices.IUnion` | `object? Value { get; }`: the active case |
| STJ | `JsonTypeInfoKind.Union` | New contract kind |
| STJ | `JsonTypeInfo.UnionCases` (`IList<JsonUnionCaseInfo>`) | `CaseType`, `IsNullable` for each case |
| STJ | `JsonTypeInfo.UnionDeconstructor` | `object -> (Type caseType, object? value)` |
| STJ | `JsonTypeInfo.UnionConstructor` | `(Type caseType, object? value) -> object` |
| STJ | `JsonTypeClassifier` | Delegate `Type? (ref Utf8JsonReader)`: picks a case **when reading** |
| STJ | `JsonTypeClassifierFactory`, `JsonTypeClassifierContext`, `JsonTypeClassifierKind` (`Union`, `PolymorphicType`) | Plug-in point for classifiers |
| STJ | `JsonUnionTypeStructuralClassifier` | Built-in classifier that chooses by property names |
| STJ | `JsonSerializerOptions.TypeClassifiers` | Classifiers that apply to every type in the options |
| STJ | `[JsonUnion(TypeClassifier = typeof(...))]` | Sets the classifier for one union type |
| STJ | `JsonSourceGenerationOptions.TypeClassifiers` | The same, for a source-gen context |
| STJ | `JsonMetadataServices.CreateUnionInfo<T>` / `JsonUnionInfoValues<T>` | What the source generator emits |

## What a union is at runtime (`UnionShapeTests`)

- `union Pet(Cat, Dog);` compiles to a **struct** carrying `[Union]` and implementing `IUnion`.
  Its public members are one constructor per case and `Value`. There is no tag, no `TryGetValue`
  and no case list.
- Boxed to `object`, it **is** `IUnion`, and `Value` is the active case. `default(Pet)` has a
  `null` `Value`.
- **A trap for #1053:** on a variable typed as the union, C# union patterns test the case.
  `pet is IUnion` is `false` and `pet is Dog` is `true`. Detection code must test `object`
  (JasperFx's `Append(object)` path already does), `Type`, or an unconstrained generic `T`.
  Never test a union-typed variable. `x is IUnion` inside `IsUnion<T>(T x)` called with a `Pet`
  sees the union and returns `true`.
- A hand-written `[Union]` struct implementing `IUnion` gets exactly the same STJ treatment as a
  keyword union.
- There are two ways to list the cases, and they agree:
  1. Reflection only: `[Union]` on the type, then each single-parameter public constructor's
     parameter type. This needs no serializer options, so projection discovery can use it.
  2. `options.GetTypeInfo(t).UnionCases`. This also reports which cases are nullable
     (`union MaybeCat(Cat?, Dog)` gives `Cat` with `IsNullable == true`).

## 1. Reflection serialization, default options (`ReflectionSerializationTests`)

- **Writing emits the active case alone**: no wrapper object and no discriminator.
  `Pet(Cat("Tom"))` writes `{"Name":"Tom"}`, `IntOrString(5)` writes `5`, and `default(Pet)`
  writes `null`. The same holds boxed as `object` and nested in a document.
- **The default classifier reads by JSON token kind only.** Number, string, true/false, array
  and object each go to the one case that accepts that kind:
  - `union IntOrString(int, string)` round-trips. `union NumberOrFlagOrList(double, bool, int[])`
    round-trips too.
  - A token kind that no case accepts throws `JsonException`: "JSON token type 'True' is not
    supported for union type …".
  - JSON `null` reads as the default union (`Value == null`).
- **Two or more object cases cannot be read back with default options.** It does not matter
  whether the shapes overlap (`Pet`), are identical (`Length(Meters, Feet)`) or are disjoint.
  The read throws `JsonException`: "JSON value type 'Object' is ambiguous for union type … Specify
  a custom type classifier". So with default options, writing succeeds and the round trip fails.

## 2. Source generation (`SourceGenerationTests`)

- The generator emits real union metadata (`CreateUnionInfo`), with `Kind == Union`, the case
  list and the deconstructor. It does not fall back to reflection.
- At run time it **behaves exactly like reflection**: the same JSON written, the same ambiguity
  failure, and the same token-kind reads for primitive cases.
- It surfaces the ambiguity **at build time**: `warning SYSLIB1227: Union type 'AccountEvent':
  case types 'Deposited', 'Withdrawn', 'Frozen' all serialize as JSON value type 'Object'. Set
  JsonUnionAttribute.TypeClassifier …`. You can see it when building this project. Reflection
  reports nothing until the first read.
- Classifiers work through the generator in both places: context-wide through
  `[JsonSourceGenerationOptions(TypeClassifiers = [typeof(JsonUnionTypeStructuralClassifier)])]`,
  and per type through `[JsonUnion(TypeClassifier = …)]`, including a custom classifier.

## 3. Discriminator vs structural (`DiscriminatorVsStructuralTests`)

### Structural: `JsonUnionTypeStructuralClassifier`

Enable it per options (`options.TypeClassifiers.Add(new JsonUnionTypeStructuralClassifier())`),
per type (`[JsonUnion(TypeClassifier = typeof(JsonUnionTypeStructuralClassifier))]`) or per
source-gen context.

- It chooses a case by the property names present, and it follows the naming policy (camelCase
  works).
  - Disjoint cases work: `Lifecycle(Opened(Owner), Closed(At))`.
  - So do cases that share one property and each have a distinct one:
    `CartEvent(ItemAdded(Id, Quantity), ItemRemoved(Id, Reason))`.
- When no case fits, or a payload has property names from several cases, it fails **per
  payload**: `JsonException` "The custom type classifier … returned null". Examples are `{}`, an
  unknown name, both cases' names, or only the shared `Id`.
- When one case's property names are a superset of another's, it **refuses the whole union up
  front**. That happens on the first contract build, so even a *write* throws `NotSupportedException`
  ("case type 'Cat' can never be selected uniquely. The case type 'Dog' recognizes every property
  name recognized by 'Cat'").
  - This refuses `Pet(Cat(Name), Dog(Name, Barks))` and `Length(Meters(Value), Feet(Value))`.
  - **It also refuses the typical event union**, `AccountEvent(Deposited(Amount),
    Withdrawn(Amount, Reason), Frozen(FrozenBy))`. Expect real event unions to hit this often.
- **`required` members on the larger case make a superset shape selectable**.
  `YoungPet(Kitten(Name), Puppy { required Name; required Barks })` reads `{"Name":…}` as Kitten
  and `{"Name":…,"Barks":…}` as Puppy. `[JsonUnmappedMemberHandling(Disallow)]` on the smaller
  case did **not** make `Pet` acceptable on RC3.
- For non-object cases it still classifies by token kind.

### Discriminator

- **No built-in mode writes a discriminator for a union.** Every mode above writes the bare case.
- `[JsonPolymorphic]` / `[JsonDerivedType]` do not compile on a union: CS0592, "valid only on
  class, interface", because unions are structs. Setting `JsonTypeInfo.PolymorphismOptions` from
  a contract modifier compiles, then throws `InvalidOperationException` at first use: "Polymorphic
  types cannot be structs".
- A `JsonTypeClassifier` only **reads** (`ref Utf8JsonReader -> Type`), so it cannot write a
  discriminator. A discriminator round trip needs the case types to write it themselves. The
  spike's `TaggedDeposited` / `TaggedWithdrawn` have a `$kind` property, and the custom
  `KindPropertyClassifier` reads it back wherever it appears in the object. Cases with identical
  shapes then round-trip. A hand-written converter would be the other way.
- A class hierarchy with `[JsonPolymorphic]` still writes `"$type"`, but a union over the same
  leaf types writes the bare case. The two features do not combine.

### What is stored, by mode (what weasel#744's type-test SQL has to match)

| Mode | JSON stored for `Union(Case)` | How SQL can tell the case |
|---|---|---|
| Default (token kind) | bare case | Primitive cases: by JSON type (PostgreSQL `jsonb_typeof`, SQLite `json_type`; SQL Server has no single type function, so combine `JSON_QUERY` / `JSON_VALUE` / `ISJSON`). Object cases: **cannot** be told apart by type |
| Structural classifier | bare case (same bytes as default) | Property presence: a case's distinguishing property exists (`?` / `jsonb_path_exists`, `JSON_PATH_EXISTS`, `json_type(data, '$.Prop') IS NOT NULL`). Absence matters too when one shape contains another |
| Discriminator (case types carry it) | case including its own `$kind` | `data->>'$kind' = 'withdrawn'` and its equivalents |

A classifier never changes the bytes that are written, so changing the classifier later does not
invalidate stored data. It can make a write *fail*, though: the structural classifier refuses an
overlapping union even when writing.

## 4. Union events (`UnionEventTests`)

- **Today, with no unwrapping:** appending `AccountEvent(Deposited)` as `object` gives the event
  type name `account_event`. `Event<AccountEvent>.EventType` is the union. Every case would share
  one name, and the stored JSON carries no case marker.
- **Unwrapping on Append** (`object` → `IUnion.Value`, repeated for nested unions):
  - The case gets its own name (`deposited`, `frozen`) and its own type.
  - The stored JSON is **byte-identical** to serializing the union, because STJ already writes
    the bare case. Unwrapping changes the event type name and the `IEvent` type, not the data.
  - A default union (`Value == null`) has no case to store. The spike's unwrap rejects it.
- **Reading back needs no classifier.** The store knows the case type from the event type name,
  so `Deserialize(json, typeof(Withdrawn))` works with plain options. That holds even for
  `AccountEvent`, which no built-in classifier can read as a union. Reading stored case JSON
  **as the union** needs the structural classifier for disjoint cases, and is impossible for
  overlapping ones. This is the strongest argument for #1053's "Append unwraps the active case":
  the JSON cannot identify the case, but the event type name can.
- **`Apply(AccountEvent e)` discovery:** the parameter type carries `[Union]`, so expand it to
  its case types (`Deposited`, `Withdrawn`, `Frozen`) and register the method as the handler for
  each. To dispatch, wrap the stored case back into the union in one of three ways:
  - `Activator.CreateInstance(unionType, caseValue)`, since every case has a public constructor.
  - `JsonTypeInfo.UnionConstructor(caseType, value)`.
  - In generated code, by passing the case directly: the compiler converts a case value
    implicitly to the union (`account.Apply(new Deposited(2m))` compiles).

## Recommendations for the downstream issues

**jasperfx#1053**
- Detect a union by `[UnionAttribute]` on the `Type` or `IUnion` on the boxed value. List cases
  from the single-parameter constructors; this needs no STJ options at discovery time.
- Unwrap with `IUnion.Value` in `Append`, looping for nested unions and rejecting a default
  union.
- Put all of it behind `#if NET11_0_OR_GREATER`.
- Avoid `is IUnion` on any statically union-typed variable. On `object` and on generic `T` it is
  safe.
- For `Apply(union)`, register one handler per case. Generated code can pass the case straight
  through, relying on the implicit conversion.

**weasel#744**
- Event tables need no union SQL, because the event type column identifies the case once events
  are unwrapped.
- Union SQL is only for **document members** typed as a union. There:
  - Primitive cases are told apart by JSON type.
  - Object cases are told apart by property presence or absence, or by a discriminator only when
    the case types write one.
  - The distinguishing property names can come from `JsonTypeInfo.UnionCases` →
    `GetTypeInfo(caseType).Properties`, which already applies the naming policy.
- Do not assume a discriminator exists: STJ never writes one for a union.

**Stores' serializer setup (Marten/Polecat/Fisher)**
- Consider registering `JsonUnionTypeStructuralClassifier` in the default STJ options, so
  documents with disjoint-case union members can be loaded.
- It is opt-in on purpose: it throws `NotSupportedException` on the first use of any union whose
  shapes overlap, even for writes, so turning it on globally can break a union that currently
  only gets written.
