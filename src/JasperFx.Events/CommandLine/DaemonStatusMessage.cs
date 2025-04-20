using JasperFx.Events.Projections;

namespace JasperFx.Events.CommandLine;

public record DaemonStatusMessage(Uri SubjectUri, string DatabaseName, ShardState State);