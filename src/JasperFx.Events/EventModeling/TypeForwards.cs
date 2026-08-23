using System.Runtime.CompilerServices;
using JasperFx.Events.EventModeling;

// jasperfx#693: the Event Modeling *wire descriptors* now live in the JasperFx assembly
// (src/JasperFx/Descriptors/EventModeling) so JasperFx's own descriptors — HttpChainDescriptor
// above all — can carry an EventModelSliceDescriptor next to the route they describe. The
// namespace did not change, so nothing anyone wrote needs editing; these forwards keep assemblies
// that were compiled against the old home binding without a recompile.
[assembly: TypeForwardedTo(typeof(AggregateDescriptor))]
[assembly: TypeForwardedTo(typeof(AggregateKind))]
[assembly: TypeForwardedTo(typeof(EventModelDescriptor))]
[assembly: TypeForwardedTo(typeof(EventModelEdge))]
[assembly: TypeForwardedTo(typeof(EventModelElement))]
[assembly: TypeForwardedTo(typeof(EventModelElementKind))]
[assembly: TypeForwardedTo(typeof(EventModelLane))]
[assembly: TypeForwardedTo(typeof(EventModelPalette))]
[assembly: TypeForwardedTo(typeof(EventModelSliceDescriptor))]
[assembly: TypeForwardedTo(typeof(ExternalSystemDescriptor))]
[assembly: TypeForwardedTo(typeof(ExternalSystemDirection))]
[assembly: TypeForwardedTo(typeof(HandlerRelationshipDescriptor))]
[assembly: TypeForwardedTo(typeof(HotspotDescriptor))]
[assembly: TypeForwardedTo(typeof(HotspotOrigin))]
[assembly: TypeForwardedTo(typeof(PublisherKind))]
[assembly: TypeForwardedTo(typeof(PublisherOrigin))]
[assembly: TypeForwardedTo(typeof(SlicePattern))]
[assembly: TypeForwardedTo(typeof(SpecificationDescriptor))]
[assembly: TypeForwardedTo(typeof(TriggerKind))]
