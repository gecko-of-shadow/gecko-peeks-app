using System.Net;
using GeckoPeeks.Cloud;

namespace GeckoPeeks.Net;

/// <summary>What a reverse lookup found: a name, a definite "no name", or no answer at all.</summary>
public enum PtrStatus { Name, NoName, Unknown }

public readonly record struct PtrAnswer(PtrStatus Status, string Host);

public interface IHopResolver
{
    /// <summary>"" when no name. Throws only OperationCanceledException.</summary>
    Task<string> ReverseAsync(string ip, bool allowDoh, CancellationToken cancellationToken);

    /// <summary>
    /// Same lookup, and it says whether "" means "this router has no name" (NoName) or "no answer
    /// came" (Unknown: timeout, DNS failure, DoH not allowed). Throws only OperationCanceledException.
    /// </summary>
    async Task<PtrAnswer> ReverseWithStatusAsync(string ip, bool allowDoh, CancellationToken cancellationToken)
    {
        var host = await ReverseAsync(ip, allowDoh, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(host) ? new PtrAnswer(PtrStatus.Unknown, "") : new PtrAnswer(PtrStatus.Name, host);
    }

    /// <summary>Missing key = unknown. Throws only OperationCanceledException.</summary>
    Task<IReadOnlyDictionary<string, long>> AsnAsync(IReadOnlyList<string> publicIps, CancellationToken cancellationToken);
}

/// <summary>System DNS only, no ASN. The old TestRunner.RunAsync overload uses it.</summary>
public sealed class SystemDnsResolver : IHopResolver
{
    public Task<string> ReverseAsync(string ip, bool allowDoh, CancellationToken cancellationToken) =>
        HopResolver.SystemPtrAsync(ip, cancellationToken);

    public Task<IReadOnlyDictionary<string, long>> AsnAsync(IReadOnlyList<string> publicIps, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
}

/// <summary>
/// Names hops for a test. PTR: system DNS first, then DoH for public router addresses.
/// ASN: the local cache, then one Worker call, then Team Cymru over DoH.
/// The Worker, DoH and Cymru steps run only with Cloudflare live data on.
/// Each hop name and each ASN batch has a 4 s budget. Only public router addresses go out.
/// </summary>
public sealed class HopResolver : IHopResolver
{
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(4000);
    public static readonly TimeSpan NameTtl = TimeSpan.FromHours(6);
    public static readonly TimeSpan NoNameTtl = TimeSpan.FromMinutes(30);
    public const int MaxCymruParallel = 6;

    private readonly AsnCache _cache;
    private readonly Func<bool> _cloudEnabled;
    private readonly CloudClient? _cloud;
    private readonly DohClient _doh;
    private readonly Func<string, CancellationToken, Task<string>> _systemPtr;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Host, DateTime Until)> _ptr = new(StringComparer.OrdinalIgnoreCase);

    public HopResolver(AsnCache cache, Func<bool> cloudEnabled, CloudClient? cloud, DohClient doh,
        Func<string, CancellationToken, Task<string>>? systemPtr = null)
        : this(cache, cloudEnabled, cloud, doh, systemPtr, null)
    {
    }

    internal HopResolver(AsnCache cache, Func<bool> cloudEnabled, CloudClient? cloud, DohClient doh,
        Func<string, CancellationToken, Task<string>>? systemPtr, Func<DateTime>? utcNow)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _cloudEnabled = cloudEnabled ?? (() => false);
        _cloud = cloud;
        _doh = doh ?? throw new ArgumentNullException(nameof(doh));
        _systemPtr = systemPtr ?? SystemPtrAsync;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<string> ReverseAsync(string ip, bool allowDoh, CancellationToken cancellationToken) =>
        (await ReverseWithStatusAsync(ip, allowDoh, cancellationToken).ConfigureAwait(false)).Host;

    /// <summary>
    /// Name: system DNS or DoH gave a name. NoName: DoH said the address has no name (cached 30 min).
    /// Unknown: anything else (timeout, DNS failure, DoH not allowed or failed).
    /// </summary>
    public async Task<PtrAnswer> ReverseWithStatusAsync(string ip, bool allowDoh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ip) || ip.Trim() == "*")
            return new PtrAnswer(PtrStatus.Unknown, "");
        var key = ip.Trim();

        lock (_lock)
        {
            if (_ptr.TryGetValue(key, out var hit))
            {
                if (hit.Until > _utcNow())
                    return hit.Host.Length > 0 ? new PtrAnswer(PtrStatus.Name, hit.Host) : new PtrAnswer(PtrStatus.NoName, "");
                _ptr.Remove(key);
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            var system = "";
            try
            {
                system = (await _systemPtr(key, budget.Token).ConfigureAwait(false) ?? "").Trim().TrimEnd('.');
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                system = "";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                system = "";
            }

            if (system.Length > 0 && !string.Equals(system, key, StringComparison.OrdinalIgnoreCase))
            {
                Remember(key, system, NameTtl);
                return new PtrAnswer(PtrStatus.Name, system);
            }

            // DoH sends the router address to Google and Cloudflare: only for a public address,
            // and only with Cloudflare live data on (off = the v2.0 lookups: system DNS only).
            if (!allowDoh || !PublicIp.IsPublic(key) || !SafeCloudEnabled() || budget.IsCancellationRequested)
                return new PtrAnswer(PtrStatus.Unknown, "");

            var doh = await _doh.PtrAsync(key, budget.Token).ConfigureAwait(false);
            switch (doh.Status)
            {
                case DohStatus.Name:
                    Remember(key, doh.Hostname, NameTtl);
                    return new PtrAnswer(PtrStatus.Name, doh.Hostname);
                case DohStatus.NoName:
                    Remember(key, "", NoNameTtl);
                    return new PtrAnswer(PtrStatus.NoName, "");
                default:
                    return new PtrAnswer(PtrStatus.Unknown, "");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PtrAnswer(PtrStatus.Unknown, ""); // the 4 s budget ran out
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new PtrAnswer(PtrStatus.Unknown, "");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> AsnAsync(IReadOnlyList<string> publicIps, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var wanted = (publicIps ?? Array.Empty<string>())
            .Where(PublicIp.IsPublic)
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
            return result;

        var misses = new List<string>();
        foreach (var ip in wanted)
        {
            if (_cache.Get(ip)?.Asn is { } asn)
                result[ip] = asn;
            else
                misses.Add(ip);
        }

        if (misses.Count == 0)
            return result;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            if (_cloud != null && SafeCloudEnabled())
            {
                try
                {
                    var rows = await _cloud.LookupAsnAsync(misses.Take(CloudClient.MaxIpsPerCall).ToList(), budget.Token).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        if (row.Asn is not { } asn || !misses.Contains(row.Ip, StringComparer.OrdinalIgnoreCase))
                            continue;
                        result[row.Ip.Trim()] = asn;
                        _cache.Put(row);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Budget used up.
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // The Worker is optional; Cymru still runs.
                }
            }

            // Team Cymru (through Google DoH) is a third party too: only with Cloudflare live data on.
            var rest = misses.Where(ip => !result.ContainsKey(ip)).ToList();
            if (rest.Count > 0 && SafeCloudEnabled() && !budget.IsCancellationRequested)
            {
                using var gate = new SemaphoreSlim(MaxCymruParallel);
                var found = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var tasks = rest.Select(async ip =>
                {
                    try
                    {
                        await gate.WaitAsync(budget.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        if (await _doh.CymruAsnAsync(ip, budget.Token).ConfigureAwait(false) is { } asn)
                            found[ip] = asn;
                    }
                    catch
                    {
                        // A lost lookup is unknown.
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var (ip, asn) in found)
                {
                    result[ip] = asn;
                    _cache.Put(new AsnResult(ip, asn, null, null));
                }
            }
        }
        finally
        {
            _cache.Flush();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>The v2.0 lookup: system resolver, 800 ms, "" on any failure.</summary>
    internal static Task<string> SystemPtrAsync(string ip, CancellationToken cancellationToken) =>
        SystemPtrAsync(ip, address => Dns.GetHostEntryAsync(address), cancellationToken);

    /// <summary>
    /// The 800 ms limit is enforced with WaitAsync. On Windows a reverse lookup ignores its
    /// cancellation token: a SERVFAIL answer took 4.6 to 7.6 s, which used up the whole 4 s
    /// hop budget before the DoH fallback could start. The abandoned lookup ends on its own.
    /// </summary>
    internal static async Task<string> SystemPtrAsync(
        string ip, Func<string, Task<IPHostEntry>> lookup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TestRunner.DnsTimeoutMs);
            var pending = lookup(ip);
            // A lookup that fails after the timeout must not reach UnobservedTaskException (crash.log).
            _ = pending.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var entry = await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
            return entry.HostName ?? "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "";
        }
    }

    private bool SafeCloudEnabled()
    {
        try
        {
            return _cloudEnabled();
        }
        catch
        {
            return false;
        }
    }

    private void Remember(string ip, string host, TimeSpan ttl)
    {
        lock (_lock)
        {
            if (_ptr.Count > 2048)
                _ptr.Clear();
            _ptr[ip] = (host, _utcNow() + ttl);
        }
    }
}
