using System.Text;
using System.Text.Json;
using GeckoPeeks.App;
using GeckoPeeks.Cloud;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Ui.State;

/// <summary>
/// The "services" block of the state message (v2.2): every service this build knows, whether it is
/// on, its target class in words, its newest fresh result and its fresh status feed answer. A result
/// or feed answer past its age limit is left out, so the page shows "not available", not old data.
/// </summary>
public static class ServiceStateJson
{
    public static string Build(CoreServices core, DateTime nowUtc) =>
        Build(core.Services.Known, core.ServiceSettings, core.Services, core.Feeds, core.Cloud.Enabled, nowUtc);

    internal static string Build(IReadOnlyList<ServiceInfo> known, ServiceSettings settings, ServiceProbes probes,
        ServiceFeeds feeds, bool cloudOn, DateTime nowUtc)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("cloudOn", cloudOn);
            w.WriteBoolean("consolePlaying", settings.ConsolePlaying);
            w.WriteStartArray("list");
            foreach (var s in known)
                WriteService(w, s, settings.IsOn(s.Id), probes, feeds, nowUtc);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteService(Utf8JsonWriter w, ServiceInfo s, bool on, ServiceProbes probes, ServiceFeeds feeds, DateTime now)
    {
        w.WriteStartObject();
        w.WriteString("id", s.Id);
        w.WriteString("name", s.Name);
        w.WriteString("group", s.Group);
        w.WriteBoolean("on", on);
        w.WriteString("classId", ServiceInfo.ClassId(s.Class));
        w.WriteString("classText", s.ClassText);
        w.WriteString("ownerName", s.Owner.Name);
        w.WriteStartArray("ownerAsns");
        foreach (var a in s.Owner.Asns)
            w.WriteNumberValue(a);
        w.WriteEndArray();
        w.WriteString("targetKind", s.Target.Kind);
        w.WriteString("targetLabel", s.Target.Label);
        w.WriteString("caveat", s.Caveat);
        w.WriteString("statusNote", s.StatusNote);

        w.WriteBoolean("hasFeed", s.Status != null);
        var feedId = feeds.FeedFor(s);
        if (feedId == null)
            w.WriteNull("feedId");
        else
            w.WriteString("feedId", feedId);
        var answer = on ? feeds.Status(feedId) : null;
        if (answer is { HasStatus: true })
        {
            w.WritePropertyName("feed");
            w.WriteRawValue(answer.Json, skipInputValidation: false);
            w.WriteNumber("feedAgeSec", Math.Max(0, Math.Round((now - answer.FetchedUtc).TotalSeconds)));
        }
        else
        {
            w.WriteNull("feed");
        }

        var last = on ? probes.Last(s.Id) : null;
        if (last != null)
            w.WriteNumber("lastAgeSec", Math.Max(0, Math.Round((now - last.AtUtc).TotalSeconds)));
        else
            w.WriteNull("lastAgeSec");
        var fresh = last != null && now - last.AtUtc <= ServiceProbes.ResultMaxAge ? last : null;
        if (fresh == null)
            w.WriteNull("result");
        else
            WriteResult(w, fresh, now);
        w.WriteEndObject();
    }

    private static void WriteResult(Utf8JsonWriter w, ServiceResult r, DateTime now)
    {
        w.WriteStartObject("result");
        w.WriteNumber("ageSec", Math.Max(0, Math.Round((now - r.AtUtc).TotalSeconds)));
        w.WriteString("outcome", r.Outcome);
        w.WriteString("reason", r.Reason);
        w.WriteString("targetIp", r.TargetIp);
        w.WriteString("targetName", r.TargetName);
        Num(w, "targetAsn", r.TargetAsn);
        if (r.OwnerMatch is { } match)
            w.WriteBoolean("ownerMatch", match);
        else
            w.WriteNull("ownerMatch");
        w.WriteString("popHow", r.Pop?.How ?? "");
        w.WriteString("popCode", r.Pop?.PopCode ?? "");
        if (r.Row is { } row)
        {
            Num(w, "targetAvgMs", ServiceProbes.Num(row.RiotHopAvg));
            Num(w, "targetLossPct", ServiceProbes.Num(row.RiotHopLossPct));
            Num(w, "lastHopAvgMs", ServiceProbes.Num(row.LastHopAvg));
            Num(w, "lastHopLossPct", ServiceProbes.Num(row.LastHopLossPct));
            w.WriteString("lastHopIp", row.LastHopIP);
            w.WriteString("upstream", row.Upstream);
            w.WriteString("handoffNetwork", row.HandoffNetwork);
            w.WriteString("handoffCity", row.HandoffCity);
            var names = r.HopNames.ToDictionary(n => n.Number);
            w.WriteStartArray("hops");
            foreach (var hop in row.Hops)
            {
                w.WriteStartObject();
                w.WriteNumber("n", hop.Number);
                w.WriteString("ip", hop.Ip);
                w.WriteString("host", hop.Hostname);
                Num(w, "rttMs", hop.RttMs);
                w.WriteBoolean("responded", hop.Responded);
                w.WriteBoolean("reachedTarget", hop.ReachedTarget);
                names.TryGetValue(hop.Number, out var name);
                w.WriteString("network", name?.Network ?? "");
                Num(w, "asn", name?.Asn);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void Num(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v)
            w.WriteNumber(name, v);
        else
            w.WriteNull(name);
    }

    private static void Num(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
            w.WriteNumber(name, v);
        else
            w.WriteNull(name);
    }
}
