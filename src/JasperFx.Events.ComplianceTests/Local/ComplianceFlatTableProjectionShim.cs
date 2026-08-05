// NOT PACKAGED. The JasperFx repo's own copy of the per-consumer partial that
// FlatTableProjectionCompliance requires -- see ComplianceFlatTableProjectionPlaceholder.cs for the
// surface it binds to, and for why this suite needs a partial rather than a global alias.

namespace JasperFx.Events.ComplianceTests;

public partial class ComplianceFlatTableProjection: Local.PlaceholderFlatTableProjection;
