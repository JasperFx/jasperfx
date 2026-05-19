// AOT smoke test (jasperfx#213).
//
// This program touches a representative cross-section of the AOT-clean
// JasperFx + JasperFx.Events surface. The csproj sets IsAotCompatible=true
// and promotes the AOT analyzer warning codes to errors, so any change that
// adds [RequiresDynamicCode] / [RequiresUnreferencedCode] to an API exercised
// here — or any change to this file that calls into a reflective JasperFx
// surface — fails the build in CI.
//
// Intentionally *not* exercised here (those carry AOT annotations by design):
//   - CommandFactory / CommandExecutor / CommandLineHostingExtensions
//     (reflective command discovery; AOT-clean path is the source-generated
//     DiscoveredCommands manifest, which is itself the smoke test for the
//     JasperFx.SourceGenerator's CommandLine output)
//   - GenericFactoryCache.BuildAs<T> (the delegate-factory overloads are
//     annotated [RequiresDynamicCode] because the default factory calls
//     MakeGenericType; an AOT consumer supplies its own AOT-safe factory)
//   - SnapshotGate.Read / Write (System.Text.Json without a generation
//     context — Marten and Wolverine consumers wrap these with their own
//     STJ context or pre-serialized strings)

using JasperFx;
using JasperFx.CodeGeneration.Snapshots;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

// --- SnapshotGate.ComputeHash / Verify ----------------------------------
// Pure functions that compute SHA-256 over a canonical-input string and
// compare fingerprints. The AOT-clean substrate the codegen-snapshot
// contract (#243) is built on.

const string sampleInput = "marten-version=9.0.0␞store-name=AppDb";

string hashA = SnapshotGate.ComputeHash(sampleInput);
string hashB = SnapshotGate.ComputeHash(sampleInput);
if (hashA != hashB)
{
    Console.Error.WriteLine($"SnapshotGate.ComputeHash is non-deterministic: '{hashA}' vs '{hashB}'.");
    return 1;
}

var live = new SnapshotFingerprint(
    ProductName: "marten",
    ProductVersion: "9.0.0-alpha.1",
    JasperFxVersion: "2.0.0-alpha.10",
    ConfigHash: hashA,
    SchemaVersion: SnapshotGate.CurrentSchemaVersion);

SnapshotVerdict firstBoot = SnapshotGate.Verify(live, persisted: null);
SnapshotVerdict accept = SnapshotGate.Verify(live, persisted: live);
SnapshotVerdict reject = SnapshotGate.Verify(live, persisted: live with { ConfigHash = "deadbeef" });

if (firstBoot != SnapshotVerdict.FirstBoot ||
    accept != SnapshotVerdict.Accept ||
    reject != SnapshotVerdict.RejectAndRegenerate)
{
    Console.Error.WriteLine(
        $"SnapshotGate.Verify regression: firstBoot={firstBoot}, accept={accept}, reject={reject}.");
    return 1;
}

// --- JasperFx.Events.Event.For<T> ---------------------------------------
// Generic factory for IEvent<T>; AOT-clean for closed-over T.

IEvent<SampleEvent> evt = Event.For(new SampleEvent("hello"));
IEvent<SampleEvent> tenantEvt = Event.For("tenant-a", new SampleEvent("hello"));
if (evt.Data.Message != tenantEvt.Data.Message)
{
    Console.Error.WriteLine("Event.For<T> regression.");
    return 1;
}

// --- CritterStackDefaults / AddJasperFx --------------------------------
// Regression for #312. These are the unified 2.0 codegen-mode entry points
// the per-store opts.GeneratedCodeMode obsoletion recommends as replacement.
// They previously carried [RequiresUnreferencedCode] which fired IL2026
// for every IsAotCompatible consumer even when JasperFx.SourceGeneration
// was wired — locking AOT consumers onto the obsolete per-store form.
// The annotation has been pushed down to the inner fallback path that
// only runs when the source-generated DiscoveredCommands manifest is absent.
// Per the AOT publishing guide, set the application assembly explicitly
// before calling CritterStackDefaults to short-circuit the stack-walk
// fallback in JasperFxOptions.

JasperFxOptions.RememberedApplicationAssembly = typeof(SampleEvent).Assembly;

var services = new ServiceCollection();
services.CritterStackDefaults(jfx =>
{
    jfx.ServiceName = "jasperfx-aot-smoke";
});

Console.WriteLine($"JasperFx AOT smoke OK — ConfigHash={hashA[..16]}…");
return 0;

internal readonly record struct SampleEvent(string Message);
