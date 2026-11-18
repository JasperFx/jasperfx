namespace JasperFx.Events.ComplianceTests;

// jasperfx#1222: the store's own Backfill(...) syntax, which the shared backfill suite cannot write for it
public partial class ComplianceBackfillProjection
{
    partial void declareBackfill()
        => Backfill(BackfillName).On<BackfillLineAdded>((doc, e) => doc.LineTotal += e.Amount);
}
