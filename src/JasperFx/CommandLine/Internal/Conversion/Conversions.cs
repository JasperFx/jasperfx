using System.Globalization;
using JasperFx.Core;

namespace JasperFx.CommandLine.Internal.Conversion;

public class Conversions
{
    private readonly LightweightCache<Type, Func<string, object?>?> _convertors;
    private readonly IList<IConversionProvider> _providers = new List<IConversionProvider>();


    public Conversions()
    {
        _convertors =
            new LightweightCache<Type, Func<string, object?>?>(
                type => { return providers().Select(x => x.ConverterFor(type)).FirstOrDefault(x => x != null); });

        RegisterConversion(bool.Parse);
        RegisterConversion(char.Parse);

        // Numbers on the command line are read the same way on every machine, so "1.5" is
        // one and a half whatever the current culture's decimal separator is
        RegisterConversion(x => byte.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => sbyte.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => decimal.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => double.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => float.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => short.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => int.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => long.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => ushort.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => uint.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(x => ulong.Parse(x, CultureInfo.InvariantCulture));
        RegisterConversion(DateTimeConverter.GetDateTime);
        RegisterConversion(Guid.Parse);

        RegisterConversion(x =>
        {
            if (x == "EMPTY")
            {
                return string.Empty;
            }

            return x;
        });
    }


    private IEnumerable<IConversionProvider> providers()
    {
        foreach (var provider in _providers) yield return provider;

        yield return new EnumerationConversion();
        yield return new NullableConvertor(this);
        yield return new ArrayConversion(this);
        yield return new StringConverterProvider();
    }

    public void RegisterConversionProvider<T>() where T : IConversionProvider, new()
    {
        _providers.Add(new T());
    }

    public void RegisterConversion<T>(Func<string, T> convertor)
    {
        _convertors[typeof(T)] = x => convertor(x);
    }

    public Func<string, object?>? FindConverter(Type type)
    {
        return _convertors[type];
    }

    public object? Convert(Type type, string raw)
    {
        return _convertors[type]?.Invoke(raw);
    }

    public bool Has(Type type)
    {
        return _convertors.Contains(type) || providers().Any(x => x.ConverterFor(type) != null);
    }
}