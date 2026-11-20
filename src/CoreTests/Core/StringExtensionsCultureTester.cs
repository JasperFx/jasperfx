using System.Globalization;
using JasperFx.Core;
using Shouldly;

namespace CoreTests.Core;

public class StringExtensionsCultureTester
{
    // tr-TR and az-AZ lower an uppercase I to a dotless ı
    private static void inTurkish(Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void table_alias_does_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            "InvoiceIssued".ToTableAlias().ShouldBe("invoice_issued");
            "IsActive".ToTableAlias().ShouldBe("is_active");
        });
    }

    [Fact]
    public void equals_ignore_case_does_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            "Inline".EqualsIgnoreCase("inline").ShouldBeTrue();
            "INVOICES".EqualsIgnoreCase("invoices").ShouldBeTrue();
            "invoices".EqualsIgnoreCase("Invoices").ShouldBeTrue();
            "Inline".EqualsIgnoreCase("Outline").ShouldBeFalse();
        });
    }

    [Fact]
    public void starts_with_ignore_case_does_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            "InvoiceIssued".StartsWithIgnoreCase("invoice").ShouldBeTrue();
            "invoiceIssued".StartsWithIgnoreCase("INVOICE").ShouldBeTrue();
            "InvoiceIssued".StartsWithIgnoreCase("issued").ShouldBeFalse();
        });
    }

    [Fact]
    public void ends_with_ignore_case_does_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            "InvoiceIssued".EndsWithIgnoreCase("issued").ShouldBeTrue();
            "invoiceissued".EndsWithIgnoreCase("ISSUED").ShouldBeTrue();
            "InvoiceIssued".EndsWithIgnoreCase("invoice").ShouldBeFalse();
        });
    }
}
