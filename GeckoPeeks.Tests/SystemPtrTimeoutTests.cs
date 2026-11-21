using System.Diagnostics;
using System.Net;
using GeckoPeeks.Net;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// On Windows a reverse lookup ignores its cancellation token (a SERVFAIL took 4.6 to 7.6 s).
/// The system PTR step must still give up at 800 ms, so the DoH fallback gets its time.
/// </summary>
public sealed class SystemPtrTimeoutTests
{
    [Fact]
    public async Task A_lookup_that_ignores_cancellation_gives_up_at_the_dns_timeout()
    {
        var never = new TaskCompletionSource<IPHostEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();

        var name = await HopResolver.SystemPtrAsync("198.51.100.7", _ => never.Task, CancellationToken.None);

        clock.Stop();
        Assert.Equal("", name);
        Assert.InRange(clock.ElapsedMilliseconds, TestRunner.DnsTimeoutMs - 100, 2500);
    }

    [Fact]
    public async Task A_quick_answer_is_returned()
    {
        var entry = new IPHostEntry { HostName = "ae1.example.net", AddressList = Array.Empty<IPAddress>() };

        var name = await HopResolver.SystemPtrAsync("198.51.100.7", _ => Task.FromResult(entry), CancellationToken.None);

        Assert.Equal("ae1.example.net", name);
    }

    [Fact]
    public async Task An_outer_cancel_still_throws()
    {
        var never = new TaskCompletionSource<IPHostEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var outer = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HopResolver.SystemPtrAsync("198.51.100.7", _ => never.Task, outer.Token));
    }

    [Fact]
    public async Task The_hop_resolver_reaches_doh_when_system_dns_hangs()
    {
        // The system step hangs past its own timeout. The 4 s hop budget must still leave time for DoH.
        var handler = new PtrHandler("xe-1-0-0.cr9.lab1.example.net.");
        using var doh = new DohClient(handler);
        var resolver = new HopResolver(
            new GeckoPeeks.Cloud.AsnCache(null), () => true, null, doh,
            (ip, token) => HopResolver.SystemPtrAsync(ip, _ => new TaskCompletionSource<IPHostEntry>().Task, token));

        // A public address (DoH refuses documentation and private ranges). Cloud data is on.
        var name = await resolver.ReverseAsync("184.105.80.9", allowDoh: true, CancellationToken.None);

        Assert.Equal("xe-1-0-0.cr9.lab1.example.net", name);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class PtrHandler : HttpMessageHandler
    {
        private readonly string _host;
        public int Calls;

        public PtrHandler(string host) => _host = host;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var body = "{\"Status\":0,\"Answer\":[{\"name\":\"9.100.51.198.in-addr.arpa.\",\"type\":12,\"TTL\":60,\"data\":\"" + _host + "\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
