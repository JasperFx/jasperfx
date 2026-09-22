# Tenant Id Casing

Is `Acme` the same tenant as `acme`? Across the Critter Stack today the honest answer is *it depends
on which component you ask*, and this page is the one place that says what each of them does. The
enum at the centre of it — `JasperFx.MultiTenancy.TenantIdStyle` — lives here, so the side-by-side
belongs here too.

## TenantIdStyle

```csharp
public enum TenantIdStyle
{
    CaseSensitive,   // the default: use the id exactly as supplied
    ForceUpperCase,  // quietly upper-case every supplied id
    ForceLowerCase   // quietly lower-case every supplied id
}
```

A component that honours the setting calls `TenantIdStyle.MaybeCorrectTenantId(tenantId)` at the
boundary where a tenant id arrives, which normalizes the id *before* anything stores or looks it up.
The default tenant id is passed through untouched whatever the style.

## What each component actually does

| Component | Honours `TenantIdStyle`? | Effect of `Acme` vs `acme` |
|---|---|---|
| **Marten** | Yes — `StoreOptions.TenantIdStyle`, applied on every session and on store-level lookups | Default `CaseSensitive`, so they are **two different tenants** |
| **Wolverine** | Yes — `opts.Durability.TenantIdStyle`, applied to the envelope and to `IMessageBus.TenantId` | Default `CaseSensitive`, so they are **two different tenants** |
| **Polecat** | No — the setting does not exist | Database-per-tenant lookup is `OrdinalIgnoreCase`, so both find the **same database**; the conjoined `tenant_id` column comparison is exact, so rows written under the two spellings **do not see each other** |
| **Fisher** | No — the setting does not exist | Same as Polecat: `OrdinalIgnoreCase` for the tenant's file, exact comparison for the conjoined `tenant_id` |

Four behaviours, and two of them are split down the middle: on Polecat and Fisher a mixed-case id
finds the right database and then writes rows that a query under the other spelling will not return.
Nothing refuses it.

## The two failures this produces

**A Wolverine host in front of a Marten store, configured differently.** Set
`opts.Durability.TenantIdStyle = TenantIdStyle.ForceLowerCase` on the host and leave Marten at its
default, and every mixed-case tenant produces `UnknownTenantIdException` — Wolverine rewrote the id
and Marten did not. If you set the style at all, set it to the same value on both.

**Split rows on Polecat and Fisher.** `Acme` and `acme` resolve to one database, so nothing looks
wrong at connection time, and then the conjoined `tenant_id` filter separates the rows. This surfaces
much later, as missing data rather than as an error.

## What to do today

Normalize tenant ids at the edge of your own system — wherever a tenant id is first read off a
request, a token or a message — and hand the Critter Stack ids that are already in one case. That is
the only rule that holds across all four components.

If you do use `TenantIdStyle`, set the same value on every component that has it, and remember that
Polecat and Fisher do not have it at all: for those two, edge normalization is the *only* protection
against split conjoined rows.

::: tip
Case is also one of the three things `UnknownTenantIdException` now tells you to check, alongside a
tenant that was never registered and (on stores that do not distinguish it) one that was deliberately
disabled.
:::

## Where this is going

The direction is for every store to honour `JasperFxOptions.TenantIdStyle` — one knob, one answer —
with Polecat and Fisher applying `MaybeCorrectTenantId` at the session and tenancy boundary and
keeping their case-insensitive lookups as a fallback. That is a behaviour change in two products and
is tracked in their own repositories; until it lands, the table above is the specification.
