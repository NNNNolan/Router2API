using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using SqlSugar;

namespace Router.Infrastructure.Services;

/// <summary>按 UTC 天和平台持久化轻量用量聚合。</summary>
public sealed class UsageBucketStore(SqlSugarDatabase database) : IUsageBucketStore
{
    public Task RecordAsync(RequestLog log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        cancellationToken.ThrowIfCancellationRequested();

        var day = log.CreatedAt.UtcDateTime.Date;
        var platform = string.IsNullOrWhiteSpace(log.Platform) ? "unknown" : log.Platform;
        var id = $"{day:yyyyMMdd}:{platform}";
        using var db = database.CreateClient();
        db.Ado.ExecuteCommand(
            """
            INSERT INTO usage_buckets
                (Id, Day, Platform, Requests, PromptTokens, CompletionTokens)
            VALUES
                (@id, @day, @platform, 1, @promptTokens, @completionTokens)
            ON CONFLICT(Day, Platform) DO UPDATE SET
                Requests = Requests + 1,
                PromptTokens = PromptTokens + excluded.PromptTokens,
                CompletionTokens = CompletionTokens + excluded.CompletionTokens
            """,
            new SugarParameter("@id", id),
            new SugarParameter("@day", day),
            new SugarParameter("@platform", platform),
            new SugarParameter("@promptTokens", log.Usage?.PromptTokens ?? 0),
            new SugarParameter("@completionTokens", log.Usage?.CompletionTokens ?? 0));
        return Task.CompletedTask;
    }
}
