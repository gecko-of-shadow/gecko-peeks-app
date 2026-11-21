using GeckoPeeks.Routing;

namespace GeckoPeeks.Text;

/// <summary>
/// Balloon text uses a city name when we know the code (den3 → Denver, sea1 → Seattle).
/// The CSV keeps the raw code. Unknown codes stay as written.
/// Lookup order: the reference data row of the provider's group, then an IATA-style row,
/// then the built-in dictionary below.
/// </summary>
public static class CityNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sea"] = "Seattle",
        ["den"] = "Denver",
        ["chi"] = "Chicago",
        ["ord"] = "Chicago",
        ["yyc"] = "Calgary",
        ["bil"] = "Billings",
        ["lax"] = "Los Angeles",
        ["sjc"] = "San Jose",
        ["sfo"] = "San Francisco",
        ["dfw"] = "Dallas",
        ["dal"] = "Dallas",
        ["iad"] = "Ashburn",
        ["ash"] = "Ashburn",
        ["nyc"] = "New York",
        ["ewr"] = "Newark",
        ["atl"] = "Atlanta",
        ["mia"] = "Miami",
        ["phx"] = "Phoenix",
        ["slc"] = "Salt Lake City",
        ["pdx"] = "Portland",
        ["yvr"] = "Vancouver",
        ["van"] = "Vancouver",
        ["yyz"] = "Toronto",
        ["tor"] = "Toronto",
        ["yul"] = "Montreal",
        ["kanc"] = "Kansas City",
        ["palo"] = "Palo Alto",
    };

    public static string Display(string? code) => Display(code, null);

    public static string Display(string? code, string? group)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";

        return Lookup(code, group, RefData.Current) ?? code;
    }

    /// <summary>Metro coordinates from the reference data, or null.</summary>
    public static (double Lat, double Lon)? Coordinates(string? code, string? group)
    {
        var row = FindRow(code, group, RefData.Current);
        return row == null ? null : (row.Lat, row.Lon);
    }

    public static bool IsKnownCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        if (Names.ContainsKey(code))
            return true;

        var letters = Letters(code);
        return letters.Length > 0 && Names.ContainsKey(letters);
    }

    /// <summary>The city name, or null when no table knows the code.</summary>
    internal static string? Lookup(string? code, string? group, RefData refData)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var row = FindRow(code, group, refData);
        if (row != null)
            return row.City;

        var letters = Letters(code);
        return letters.Length > 0 && Names.TryGetValue(letters, out var name) ? name : null;
    }

    private static CityCodeRow? FindRow(string? code, string? group, RefData refData)
    {
        if (string.IsNullOrWhiteSpace(code) || refData == null)
            return null;
        var letters = Letters(code).ToLowerInvariant();
        if (letters.Length == 0)
            return null;
        if (!string.IsNullOrWhiteSpace(group) && refData.FindCity(letters, group) is { } byGroup)
            return byGroup;
        return refData.FindCity(letters, null);
    }

    private static string Letters(string code) => new(code.Trim().TakeWhile(char.IsLetter).ToArray());
}
