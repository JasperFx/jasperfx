using JasperFx.Descriptors;

namespace JasperFx.Events.Descriptors;

public record EventDescriptor(string EventTypeName, TypeDescriptor Type);