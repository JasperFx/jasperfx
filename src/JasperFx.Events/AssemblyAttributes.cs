using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.CommandLine;

[assembly: InternalsVisibleTo("EventTests")]
[assembly: InternalsVisibleTo("InMemoryEventStoreTests")]

// jasperfx#1215: the in-memory store rewrites the event data it holds through IReplaceableEventData
[assembly: InternalsVisibleTo("JasperFx.Events.InMemory")]
[assembly: JasperFxAssembly]

