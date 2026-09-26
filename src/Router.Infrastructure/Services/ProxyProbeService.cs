using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>宿主唯一的节点质量探测器。插件不能改变 URL、响应大小或探测规则。</summary>
public sealed class ProxyProbeService(
    IProxyHttpClientFactory http,
    IProxyStore store,
    ISharedKeyValueStore runtimeState,
    ILogger<ProxyProbeService> logger) : IProxyProbeService
{
    public const string ProbeUrl = "https://speed.cloudflare.com/__down?bytes=65536";

    public async Task<ProxyProbeResult> ProbeAsync(
        ProxyEndpoint proxy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        var lockKey = $"probe-lock:{proxy.Id}:{proxy.ConfigurationVersion}";
        if (!await runtimeState.TryAcquireAsync(lockKey, TimeSpan.FromMinutes(10), cancellationToken))
            return new ProxyProbeResult(false, 0, 0, "Busy", "probe already running");

        try { return await ProbeCoreAsync(proxy, cancellationToken); }
        finally
        {
            try { await runtimeState.RemoveAsync(lockKey, CancellationToken.None); }
            catch (Exception exception) { logger.LogDebug(exception, "proxy probe lock release failed proxyId={ProxyId}", proxy.Id); }
        }
    }

    private async Task<ProxyProbeResult> ProbeCoreAsync(
        ProxyEndpoint proxy,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var client = http.CreateClient(proxy, "probe");
            using var response = await client.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return await SaveFailureAsync(proxy, stopwatch, $"HTTP {(int)response.StatusCode}", timeout.Token);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8192];
            var total = 0;
            while (total < 65536)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 65536 - total)), timeout.Token);
                if (read == 0) break;
                total += read;
            }

            stopwatch.Stop();
            var latency = Math.Max(1, (int)stopwatch.ElapsedMilliseconds);
            var speed = total / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            if (total != 65536)
                return await SaveFailureAsync(
                    proxy,
                    stopwatch,
                    $"downloaded {total} of 65536 bytes",
                    cancellationToken);

            proxy.LatencyMs = latency;
            proxy.AverageLatencyMs = proxy.AverageLatencyMs == 0
                ? latency
                : (int)Math.Round(proxy.AverageLatencyMs * 0.8 + latency * 0.2);
            proxy.AverageSpeedBytesPerSecond = proxy.AverageSpeedBytesPerSecond == 0
                ? speed
                : proxy.AverageSpeedBytesPerSecond * 0.8 + speed * 0.2;
            proxy.CompositeScore = CalculateScore(proxy.AverageLatencyMs, proxy.AverageSpeedBytesPerSecond);
            proxy.LastProbeAt = DateTimeOffset.UtcNow;
            proxy.LastProbeSuccessAt = proxy.LastProbeAt;
            proxy.ProbeConsecutiveFailures = 0;
            proxy.ProbeError = null;
            proxy.ProbeStatus = "Healthy";
            proxy.ProbeConfigurationVersion = proxy.ConfigurationVersion;
            await store.SaveAsync(proxy, cancellationToken);
            return new ProxyProbeResult(total == 65536, latency, speed, proxy.ProbeStatus);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return await SaveFailureAsync(proxy, stopwatch, exception.Message, cancellationToken);
        }
    }

    private async Task<ProxyProbeResult> SaveFailureAsync(
        ProxyEndpoint proxy,
        Stopwatch stopwatch,
        string error,
        CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        proxy.LastProbeAt = DateTimeOffset.UtcNow;
        proxy.ProbeConsecutiveFailures++;
        proxy.ProbeStatus = "Failed";
        proxy.ProbeError = error;
        proxy.ProbeConfigurationVersion = proxy.ConfigurationVersion;
        await store.SaveAsync(proxy, cancellationToken);
        logger.LogDebug("proxy probe failed proxyId={ProxyId} error={Error}", proxy.Id, error);
        return new ProxyProbeResult(false, Math.Max(1, (int)stopwatch.ElapsedMilliseconds), 0, "Failed", error);
    }

    private static double CalculateScore(int latency, double speed)
        => Math.Round((1000d / Math.Max(1, latency)) + Math.Log10(Math.Max(1, speed)), 4);
}
