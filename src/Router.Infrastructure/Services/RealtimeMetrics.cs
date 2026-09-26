using System.Collections.Concurrent;
using System.Diagnostics;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>低分配的进程指标收集器。</summary>
public sealed class RealtimeMetrics : IRealtimeMetrics
{
    private readonly ConcurrentDictionary<string, PlatformCounters> _platforms = new(StringComparer.OrdinalIgnoreCase);
    private long _activeConnections;
    private long _activeStreaming;
    private long _requests;
    private long _tokens;
    private long _promptTokens;
    private long _completionTokens;
    private long _failures;
    private long _latencyTotal;

    public void OnRequestStarted(string platform)
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _platforms.GetOrAdd(platform, _ => new()).RequestsStarted);
    }

    public void OnRequestCompleted(string platform, int durationMs, bool success, Usage? usage)
    {
        Interlocked.Decrement(ref _activeConnections);
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _latencyTotal, durationMs);
        if (!success) Interlocked.Increment(ref _failures);

        var counters = _platforms.GetOrAdd(platform, _ => new());
        Interlocked.Increment(ref counters.Requests);
        Interlocked.Add(ref counters.Latency, durationMs);
        if (usage is not null)
        {
            Interlocked.Add(ref _tokens, usage.TotalTokens);
            Interlocked.Add(ref _promptTokens, usage.PromptTokens);
            Interlocked.Add(ref _completionTokens, usage.CompletionTokens);
            Interlocked.Add(ref counters.Tokens, usage.TotalTokens);
        }
    }

    public void OnStreamStarted(string platform)
    {
        Interlocked.Increment(ref _activeStreaming);
        Interlocked.Increment(ref _platforms.GetOrAdd(platform, _ => new()).Streams);
    }

    public void OnStreamEnded(string platform) => Interlocked.Decrement(ref _activeStreaming);

    public RealtimeSnapshot Snapshot()
    {
        var requests = Interlocked.Read(ref _requests);
        var platforms = _platforms.ToDictionary(
            pair => pair.Key,
            pair => new PlatformSnapshot(
                Interlocked.Read(ref pair.Value.Requests),
                Interlocked.Read(ref pair.Value.Tokens),
                Interlocked.Read(ref pair.Value.Requests) == 0
                    ? 0
                    : (double)Interlocked.Read(ref pair.Value.Latency) / Interlocked.Read(ref pair.Value.Requests)));

        return new RealtimeSnapshot(
            DateTimeOffset.UtcNow,
            (int)Interlocked.Read(ref _activeConnections),
            (int)Interlocked.Read(ref _activeStreaming),
            requests,
            Interlocked.Read(ref _tokens),
            Interlocked.Read(ref _promptTokens),
            Interlocked.Read(ref _completionTokens),
            Interlocked.Read(ref _failures),
            requests == 0 ? 0 : (double)Interlocked.Read(ref _latencyTotal) / requests,
            0,
            0,
            platforms);
    }

    private sealed class PlatformCounters
    {
        public long RequestsStarted;
        public long Requests;
        public long Streams;
        public long Tokens;
        public long Latency;
    }
}
