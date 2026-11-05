using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Documents;
using Shouldly;

namespace InMemoryEventStoreTests;

public class compliance_suite_inventory_tests
{
    public class FlagOnFixture : DocumentStorageComplianceFixture
    {
        public override bool SupportsNumericRevisions => true;
        protected override Task BuildStoreAsync(DocumentComplianceConfig config) => Task.CompletedTask;
        public override IDocumentSessionFactory Sessions => throw new System.NotSupportedException();
        public override Task CleanDocumentDataAsync() => Task.CompletedTask;
    }

    public class FlagOffFixture : FlagOnFixture
    {
        public override bool SupportsNumericRevisions => false;
    }

    private class EnrolledOnFlagOn : NumericRevisionCompliance<FlagOnFixture>;

    private class EnrolledOnFlagOff : NumericRevisionCompliance<FlagOffFixture>;

    private static readonly Type[] Suites =
        [typeof(NumericRevisionComplianceBase<,>), typeof(NumericRevisionCompliance<>)];

    private static IReadOnlyDictionary<Type, string> Excuse() =>
        new Dictionary<Type, string> { [typeof(NumericRevisionCompliance<>)] = "because" };

    [Fact]
    public void enrolled_suite_passes()
        => ComplianceSuiteInventory.Check([..Suites, typeof(EnrolledOnFlagOn)], new FlagOnFixture())
            .ShouldBeEmpty();

    [Fact]
    public void missing_suite_without_excuse_fails()
        => ComplianceSuiteInventory.Check(Suites, new FlagOffFixture())
            .ShouldHaveSingleItem().ShouldContain("not excused");

    [Fact]
    public void subclass_over_another_fixture_does_not_count()
        => ComplianceSuiteInventory.Check([..Suites, typeof(EnrolledOnFlagOn)], new FlagOffFixture())
            .ShouldHaveSingleItem().ShouldContain("not excused");

    [Fact]
    public void excused_with_flag_off_passes()
        => ComplianceSuiteInventory.Check(Suites, new FlagOffFixture(), Excuse()).ShouldBeEmpty();

    [Fact]
    public void excused_with_flag_on_fails()
        => ComplianceSuiteInventory.Check(Suites, new FlagOnFixture(), Excuse())
            .ShouldHaveSingleItem().ShouldContain("SupportsNumericRevisions is true");

    [Fact]
    public void excused_but_enrolled_fails()
        => ComplianceSuiteInventory.Check([..Suites, typeof(EnrolledOnFlagOff)], new FlagOffFixture(), Excuse())
            .ShouldHaveSingleItem().ShouldContain("stale excuse");

    [Fact]
    public void excusing_an_unknown_suite_fails()
        => ComplianceSuiteInventory.Check([..Suites, typeof(EnrolledOnFlagOn)], new FlagOnFixture(),
                new Dictionary<Type, string> { [typeof(StreamQueryPlanCompliance<,,>)] = "x" })
            .ShouldHaveSingleItem().ShouldContain("not a compliance suite");

    [Fact]
    public void event_store_family_is_found_by_type_in_a_real_assembly()
    {
        var problems = ComplianceSuiteInventory.Check(typeof(compliance_suite_inventory_tests).Assembly.GetTypes(),
            new InMemoryComplianceFixture());

        problems.ShouldNotBeEmpty();
        problems.ShouldNotContain(x => x.StartsWith("FetchForWritingCompliance"));
        problems.ShouldContain(x => x.StartsWith("EventQueryCompliance"));
    }
}
