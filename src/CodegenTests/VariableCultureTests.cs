using System.Globalization;
using JasperFx.CodeGeneration.Model;
using Shouldly;

namespace CodegenTests;

public class InvoiceIssued;

public interface IInvoiceService;

// jasperfx#1248: pre-generated code must not differ by the culture of the machine that wrote it
public class VariableCultureTests
{
    // tr-TR lower-cases I to the dotless ı
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
    public void default_arg_names_do_not_depend_on_the_current_culture()
    {
        inTurkish(() =>
        {
            Variable.DefaultArgName<InvoiceIssued>().ShouldBe("invoiceIssued");
            Variable.DefaultArgName<IInvoiceService>().ShouldBe("invoiceService");
        });
    }
}
