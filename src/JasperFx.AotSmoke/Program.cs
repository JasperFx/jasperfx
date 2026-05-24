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

using JasperFx;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

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
// for every IsAotCompatible consumer even when JasperFx.SourceGenerator
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

Console.WriteLine("JasperFx AOT smoke OK.");
return 0;

internal readonly record struct SampleEvent(string Message);
