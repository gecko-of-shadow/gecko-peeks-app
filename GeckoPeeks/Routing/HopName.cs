namespace GeckoPeeks.Routing;

/// <param name="Network">"" when unknown; "Zayo + Telia" style for a mixed hostname.</param>
/// <param name="Source">"hostname", "asn" or "".</param>
public sealed record HopName(int Number, string Network, string Source, long? Asn, string CityCode, string CityName);
