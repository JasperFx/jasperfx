using JasperFx.Core;

namespace JasperFx.CommandLine.Parsing;

public static class OptionReader
{
    public static string Read(string file)
    {
        return File.ReadAllLines(file)
            .Select(x => x.Trim())
            .Where(x => x.IsNotEmpty())
            .Join(" ");
    }
}