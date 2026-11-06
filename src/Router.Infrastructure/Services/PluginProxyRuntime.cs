using System.Diagnostics;
using System.Text.Json;
using Polly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Router.Infrastructure.Services;

/// <summary>宿主唯一的插件尝试执行器：每次重试都会重新选择账号和节点。</summary>
public sealed class PluginAttemptExecutor(
    IAccountService accounts,
    IProxyStore proxies,
    IProxySubscriptionService subscriptions,
    IResourceLeaseManager leases,
    IProxyPolicyStore proxyPolicy,
    ISharedKeyValueStore shortTermState,
    IProxyHttpClientFactory http,
    IPluginPolicyRegistry policyRegistry,
    IModelMetadataCatalog modelMetadata,
    IPluginLogSink pluginLogs,
    ILogger<PluginAttemptExecutor> logger,
    PluginResiliencePipelines resilience,
    IOptions<PluginExecutionOptions> executionOptions) : IPluginAttemptExecutor
{
    public Task<PluginInvocationResult> ExecuteAsync(
        string pluginKey,
        string platformName,
        IPlatformTerminal terminal,
        AdapterRequest request,
        string traceId,
        TestOverrides? overrides,
        CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(pluginKey, platformName, terminal, request, traceId, overrides, null, null, cancellationToken);

    /// <summary>执行一个映射目标，只覆盖本次尝试预算，保留插件的错误决策和资源策略。</summary>
    /// <param name="pluginKey">目标插件。</param>
    /// <param name="platformName">目标平台。</param>
    /// <param name="terminal">当前平台终端。</param>
    /// <param name="request">目标独立请求。</param>
    /// <param name="traceId">原请求跟踪 ID。</param>
    /// <param name="overrides">管理员测试覆盖。</param>
    /// <param name="attemptCount">包含首次请求的总尝试次数；为空则继承插件声明。</param>
    /// <param name="setupBudget">整条映射剩余的建立响应预算。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>完整响应及该目标的尝试明细。</returns>
    public Task<PluginInvocationResult> ExecuteMappedAsync(
        string pluginKey, string platformName, IPlatformTerminal terminal, AdapterRequest request, string traceId,
        TestOverrides? overrides, int? attemptCount, TimeSpan setupBudget, CancellationToken cancellationToken = default)
    {
        if (attemptCount is < 1 or > 35) throw new ArgumentOutOfRangeException(nameof(attemptCount));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(setupBudget, TimeSpan.Zero);
        return ExecuteCoreAsync(pluginKey, platformName, terminal, request, traceId, overrides,
            attemptCount, setupBudget, cancellationToken);
    }

    /// <summary>读取宿主当前 attempt client 的实际预算，供 JS 防止业务与传输重试叠加；不改变公开插件契约。</summary>
    public static int? GetAttemptLimit(IPluginHttpClient client) => (client as PluginHttpClient)?.MaxAttempts;

    private async Task<PluginInvocationResult> ExecuteCoreAsync(
        string pluginKey, string platformName, IPlatformTerminal terminal, AdapterRequest request, string traceId,
        TestOverrides? overrides, int? maxAttempts, TimeSpan? setupBudget, CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var snapshot = policyRegistry.GetSnapshot(pluginKey, platformName);
        var policyOptions = maxAttempts is { } limit ? snapshot.Attempts with { MaxAttempts = limit } : snapshot.Attempts;
        var accountPolicy = snapshot.Accounts;
        var attemptDetails = new List<RequestAttemptDetail>();
        using var invocation = new PluginInvocationScope(policyOptions, executionOptions.Value, cancellationToken, setupBudget);
        var resilienceContext = ResilienceContextPool.Shared.Get(invocation.Token);
        resilienceContext.Properties.Set(PluginResiliencePipelines.AttemptRetries, Math.Clamp(policyOptions.MaxAttempts - 1, 0, 34));

        try
        {
            await TryWriteLogAsync(new PluginLog
            {
                PluginKey = pluginKey,
                Platform = platformName,
                EventType = "request.started",
                Message = "plugin request attempt sequence started",
                TraceId = traceId,
                Model = request.Model,
                Level = "Debug"
            });
            await ClampReasoningAsync(platformName, request, invocation.Token);
            var result = await resilience.Attempts.ExecuteAsync(
                async ctx => await ExecuteAttemptAsync(
                    pluginKey,
                    platformName,
                    terminal,
                    request,
                    traceId,
                    overrides,
                    policyOptions,
                    accountPolicy,
                    attemptDetails,
                    invocation,
                    ctx.CancellationToken),
                resilienceContext);
            if (PluginResponseLifetime.HasStream(result.Response))
            {
                var response = invocation.TransferToResponse(result.Response);
                var lifetime = (PluginResponseLifetime)response.Lifetime!;
                lifetime.OnCompleted(async completion =>
                {
                    totalStopwatch.Stop();
                    await TryWriteLogAsync(new PluginLog
                    {
                        PluginKey = pluginKey, Platform = platformName, TraceId = traceId, Model = request.Model,
                        EventType = "request.completed",
                        Message = completion.Success ? "plugin stream completed" : completion.Error ?? "plugin stream failed",
                        StatusCode = response.StatusCode,
                        DurationMs = (int)totalStopwatch.ElapsedMilliseconds,
                        Level = completion.Success ? "Debug" : "Error",
                        DetailsJson = JsonSerializer.Serialize(new { attempts = attemptDetails.Count, completion.Cancelled, completion.Usage })
                    });
                });
                return result with { Response = response, AttemptDetails = attemptDetails.ToArray() };
            }
            totalStopwatch.Stop();
            await TryWriteLogAsync(new PluginLog
            {
                PluginKey = pluginKey,
                Platform = platformName,
                EventType = "request.completed",
                Message = result.Response.IsSuccess ? "plugin request completed" : result.Response.Error ?? "plugin request failed",
                TraceId = traceId,
                Model = request.Model,
                StatusCode = result.Response.StatusCode,
                DurationMs = (int)totalStopwatch.ElapsedMilliseconds,
                Level = result.Response.IsSuccess ? "Debug" : "Error",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    outcome = result.Attempt.Outcome.ToString(),
                    attempts = attemptDetails.Count
                })
            });
            return result with { AttemptDetails = attemptDetails.ToArray() };
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            await TryWriteLogAsync(new PluginLog
            {
                PluginKey = pluginKey, Platform = platformName, TraceId = traceId, Model = request.Model,
                EventType = "request.cancelled", Message = "request cancelled by caller",
                DurationMs = (int)totalStopwatch.ElapsedMilliseconds, Level = "Warning",
                DetailsJson = JsonSerializer.Serialize(new { attempts = attemptDetails.Count, cancelled = true })
            });
            throw new PluginRequestCancelledException(exception, attemptDetails.ToArray(), cancellationToken);
        }
        catch (OperationCanceledException) when (invocation.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            await TryWriteLogAsync(new PluginLog
            {
                PluginKey = pluginKey,
                Platform = platformName,
                EventType = "request.timeout",
                Message = "plugin request timed out",
                TraceId = traceId,
                Model = request.Model,
                DurationMs = (int)totalStopwatch.ElapsedMilliseconds,
                Level = "Error"
            });
            return new PluginInvocationResult(
                AdapterResponse.ServerError("upstream request timed out"),
                setupBudget.HasValue
                    ? new PluginAttemptDecision { FailureKind = PluginFailureKind.Upstream, Retry = PluginRetryAction.NextAttempt, ReasonCode = "setup-timeout" }.ToResult(reason: "timeout")
                    : new PluginAttemptResult(PluginAttemptOutcome.NoPenalty, Reason: "timeout"),
                attemptDetails.ToArray());
        }
        catch (PluginInputTooLargeException exception)
        {
            // 调用方请求体超过插件桥接上限：归为客户端输入问题，不记为插件或上游故障。
            totalStopwatch.Stop();
            await TryWriteLogAsync(new PluginLog
            {
                PluginKey = pluginKey,
                Platform = platformName,
                EventType = "request.too_large",
                Message = exception.Message,
                TraceId = traceId,
                Model = request.Model,
                StatusCode = 413,
                DurationMs = (int)totalStopwatch.ElapsedMilliseconds,
                Level = "Warning"
            });
            return new PluginInvocationResult(
                new AdapterResponse
                {
                    StatusCode = 413,
                    Error = exception.UserMessage
                },
                new PluginAttemptResult(PluginAttemptOutcome.NoPenalty, Reason: exception.Message),
                attemptDetails.ToArray());
        }
        catch (Exception exception)
        {
            totalStopwatch.Stop();
            logger.LogError(exception, "plugin attempt execution failed pluginKey={PluginKey} traceId={TraceId}", pluginKey, traceId);
            await TryWriteLogAsync(new PluginLog
            {
                PluginKey = pluginKey,
                Platform = platformName,
                EventType = "request.exception",
                Message = exception.Message,
                TraceId = traceId,
                Model = request.Model,
                DurationMs = (int)totalStopwatch.ElapsedMilliseconds,
                Level = "Error"
            });
            return new PluginInvocationResult(
                AdapterResponse.ServerError(exception.Message),
                new PluginAttemptResult(PluginAttemptOutcome.NoPenalty, Reason: exception.Message),
                attemptDetails.ToArray());
        }
        finally
        {
            ResilienceContextPool.Shared.Return(resilienceContext);
        }
    }

    private async Task ClampReasoningAsync(
        string platformName,
        AdapterRequest request,
        CancellationToken cancellationToken)
    {
        if (!ReasoningLimiter.HasReasoning(request)) return;

        try
        {
            var metadata = await modelMetadata.FindAsync(
                platformName,
                request.Model,
                cancellationToken);
            if (metadata is not null && ReasoningLimiter.Clamp(request, metadata))
                logger.LogDebug(
                    "reasoning request clamped platform={Platform} model={Model} levels={Levels}",
                    platformName,
                    request.Model,
                    metadata.ReasoningLevels is null ? "unknown" : string.Join(',', metadata.ReasoningLevels));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 元数据查询仅作辅助，内部超时不能阻止模型请求继续执行。
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(
                exception,
                "reasoning metadata unavailable platform={Platform} model={Model}",
                platformName,
                request.Model);
        }
    }

    private async Task<PluginInvocationResult> ExecuteAttemptAsync(
        string pluginKey,
        string platformName,
        IPlatformTerminal terminal,
        AdapterRequest request,
        string traceId,
        TestOverrides? overrides,
        PluginProxyPolicy policy,
        PluginAccountPolicy accountPolicy,
        List<RequestAttemptDetail> attemptDetails,
        PluginInvocationScope invocation,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Account> accountCandidates;
        using (var stage = RequestTrace.Measure("account.selection", pluginKey, platformName, request.Model))
        {
            accountCandidates = await GetAccountsAsync(pluginKey, platformName, request, overrides, accountPolicy, cancellationToken);
            stage?.SetResult(accountCandidates.Count == 0 ? "unavailable" : "completed");
        }
        if (accountCandidates.Count == 0)
            return Failure("no account available", PluginAttemptOutcome.NoPenalty, "no-account");

        ResourceLeaseResult<Account>? accountLease = null;
        PluginHttpClient? client = null;
        CancellationTokenSource? attemptTimeout = null;
        AdapterResponse? pendingResponse = null;
        var transferred = false;
        try
        {
            using (var stage = RequestTrace.Measure("account.lease", pluginKey, platformName, request.Model))
            {
            accountLease = await leases.AcquireAsync(
                accountCandidates,
                account => overrides?.SkipCooldown == true || ResourceAvailability.IsAvailable(account, DateTimeOffset.UtcNow),
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
            stage?.SetResult(accountLease is null ? "unavailable" : "completed");
            }
            if (accountLease is null)
                return Failure("no account lease available", PluginAttemptOutcome.Retry, "no-account-lease");

            attemptTimeout = invocation.CreateAttemptTimeout();
            var bodyPhase = 0;
            void BeginBody()
            {
                if (Interlocked.Exchange(ref bodyPhase, 1) != 0) return;
                invocation.BeginResponse(attemptTimeout);
            }
            client = new PluginHttpClient(
                token => AcquireProxyBindingAsync(pluginKey, overrides, token),
                http.CreateDirectClient("upstream"),
                response =>
                {
                    if (response.IsSuccessStatusCode
                        && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) BeginBody();
                }, policy.MaxAttempts);

            var attemptStopwatch = Stopwatch.StartNew();
            PluginInvocationResult result;
            using var pluginStage = RequestTrace.Measure("plugin.attempt", pluginKey, platformName, request.Model);
            try
            {
                result = await terminal.InvokeAsync(new PluginAttemptContext
                {
                    Request = request,
                    PlatformName = platformName,
                    PluginKey = pluginKey,
                    Account = accountLease.Value.Resource,
                    HttpClient = client,
                    TraceId = traceId,
                    CancellationToken = attemptTimeout.Token,
                    NotifyUpstreamResponseStarted = BeginBody
                });
            }
            catch (OperationCanceledException) when (
                attemptTimeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                result = new PluginInvocationResult(
                    AdapterResponse.ServerError("upstream attempt timed out"),
                    HostTransportFailure(policy, "timeout", client.ObservedTransportFailure));
            }
            catch (HttpRequestException exception)
            {
                result = new PluginInvocationResult(
                    AdapterResponse.ServerError(exception.Message),
                    HostTransportFailure(policy, exception.Message, client.ObservedTransportFailure));
            }
            catch
            {
                pluginStage?.SetResult(cancellationToken.IsCancellationRequested ? "cancelled" : "failed");
                throw;
            }
            pluginStage?.SetResult(result.Response.IsSuccess ? "completed" : "failed", result.Response.StatusCode);
            pluginStage?.Dispose(); // 阶段到插件返回响应为止，响应消费不算插件建立耗时。

            pendingResponse = result.Response;
            result = result with
            {
                Attempt = PluginAttemptDecisions.Resolve(result, policy, accountPolicy, client.ObservedTransportFailure, client.ObservedProxyFault)
            };
            if (PluginResponseLifetime.HasStream(result.Response)) BeginBody();
            attemptStopwatch.Stop();
            var usedProxy = client.UsedProxy;
            var proxyId = usedProxy?.Id;
            var proxyAddress = usedProxy is null ? "direct" : FormatProxy(usedProxy);
            attemptDetails.Add(new RequestAttemptDetail(
                proxyId,
                proxyAddress,
                result.Attempt.StatusCode,
                result.Attempt.Outcome.ToString(),
                result.Attempt.IsTransportFailure,
                result.Attempt.Reason ?? result.Response.Error,
                (int)attemptStopwatch.ElapsedMilliseconds));

            await TryWriteLogAsync(new PluginLog
            {
                PluginKey = pluginKey,
                Platform = platformName,
                EventType = "upstream.attempt",
                Message = result.Attempt.Reason ?? (result.Response.IsSuccess ? "upstream request succeeded" : "upstream request failed"),
                TraceId = traceId,
                AccountId = accountLease.Value.Resource.Id,
                Model = request.Model,
                StatusCode = result.Attempt.StatusCode ?? result.Response.StatusCode,
                DurationMs = (int)attemptStopwatch.ElapsedMilliseconds,
                Level = result.Response.IsSuccess ? "Debug" : "Error",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    route = usedProxy is not null ? "proxy" : client.UsedDirect ? "direct" : "unbound",
                    proxyId,
                    proxyAddress,
                    outcome = result.Attempt.Outcome.ToString(),
                    isTransportFailure = result.Attempt.IsTransportFailure,
                    decision = result.Attempt.Decision
                })
            });

            var decision = result.Attempt.Decision!;
            var nodePolicyTask = usedProxy is { } leasedProxyForPolicy
                && (decision.ProxyAction == PluginProxyAction.Cooldown || result.Response.IsSuccess)
                ? proxyPolicy.ReportAsync(pluginKey, leasedProxyForPolicy, result.Attempt with
                {
                    Outcome = decision.ProxyAction == PluginProxyAction.Cooldown ? PluginAttemptOutcome.CooldownNode : PluginAttemptOutcome.Healthy
                }, cancellationToken)
                : Task.CompletedTask;
            var accountRuleTask = ApplyAccountDecisionAsync(pluginKey, accountLease.Value.Resource, result.Attempt, cancellationToken);
            await Task.WhenAll(nodePolicyTask, accountRuleTask);
            if (PluginResponseLifetime.HasStream(result.Response))
            {
                var response = PluginResponseLifetime.Ensure(result.Response);
                var lifetime = (PluginResponseLifetime)response.Lifetime!;
                var ownedClient = client;
                var ownedTimeout = attemptTimeout;
                var ownedLease = accountLease.Value.Lease;
                lifetime.AddResource(async () =>
                {
                    try { await ownedClient.DisposeAsync(); }
                    finally
                    {
                        ownedTimeout.Dispose();
                        await ownedLease.DisposeAsync();
                    }
                });
                transferred = true;
                pendingResponse = null;
                return result with { Response = response };
            }
            pendingResponse = null;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                try
                {
                    if (pendingResponse?.Lifetime is { } lifetime) await lifetime.DisposeAsync();
                }
                finally
                {
                    try { if (client is not null) await client.DisposeAsync(); }
                    finally
                    {
                        attemptTimeout?.Dispose();
                        if (accountLease is { } selectedAccountLease) await selectedAccountLease.Lease.DisposeAsync();
                    }
                }
            }
        }
    }

    private async Task<PluginProxyBinding?> AcquireProxyBindingAsync(
        string pluginKey,
        TestOverrides? overrides,
        CancellationToken cancellationToken)
    {
        using var stage = RequestTrace.Measure("proxy.selection", pluginKey);
        var proxyCandidates = await GetProxiesAsync(pluginKey, overrides, cancellationToken);
        var selectedProxy = SelectProxy(proxyCandidates, overrides?.ProxyId);
        if (!string.IsNullOrWhiteSpace(overrides?.ProxyId) && selectedProxy is null)
            throw new HttpRequestException("forced proxy is unavailable");
        if (selectedProxy is null)
        {
            stage?.SetResult("direct");
            return null;
        }

        var proxyLease = await leases.AcquireAsync(
            [selectedProxy],
            proxy => overrides?.SkipCooldown == true || proxy.Status.State == ResourceState.Active,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        if (proxyLease is null)
        {
            stage?.SetResult("unavailable");
            throw new HttpRequestException("no proxy lease available");
        }

        try
        {
            return new PluginProxyBinding(
                proxyLease.Value.Resource,
                http.CreateClient(proxyLease.Value.Resource, "upstream"),
                proxyLease.Value.Lease);
        }
        catch
        {
            await proxyLease.Value.Lease.DisposeAsync();
            throw;
        }
    }

    private async Task<IReadOnlyList<Account>> GetAccountsAsync(
        string pluginKey,
        string platformName,
        AdapterRequest request,
        TestOverrides? overrides,
        PluginAccountPolicy accountPolicy,
        CancellationToken cancellationToken)
    {
        if (overrides?.AccountId is { Length: > 0 } accountId)
        {
            var forced = await accounts.GetAsync(pluginKey, accountId, cancellationToken);
            return forced is null
                ? []
                : await FilterAccountsAsync(pluginKey, platformName, [forced], request, overrides, accountPolicy, cancellationToken);
        }

        // 优先级和权重应覆盖插件平台的全部账号，不能只考虑第一页可用账号。
        if (accountPolicy.PreferredExpirySelector is not null || accountPolicy.WeightSelector is not null || accountPolicy.BatchSelector is not null)
        {
            var all = accounts is AccountService store
                ? await store.QueryCandidatesAsync(pluginKey, platformName, overrides?.SkipCooldown == true,
                    DateTimeOffset.UtcNow, null, 0, cancellationToken)
                : await accounts.ListAsync(pluginKey, platformName, cancellationToken);
            return await FilterAccountsAsync(pluginKey, platformName, all, request, overrides, accountPolicy, cancellationToken);
        }

        const int pageSize = 100;
        for (var page = 1; page <= 10; page++)
        {
            var candidates = accounts is AccountService store
                ? await store.QueryCandidatesAsync(pluginKey, platformName, overrides?.SkipCooldown == true,
                    DateTimeOffset.UtcNow, page, pageSize, cancellationToken)
                : (await accounts.QueryAsync(
                    new AccountQuery(pluginKey, platformName, Page: page, PageSize: pageSize), cancellationToken)).Items;
            var result = await FilterAccountsAsync(pluginKey, platformName, candidates, request, overrides, accountPolicy, cancellationToken);
            if (result.Count > 0 || candidates.Count < pageSize)
                return result;
        }

        return [];
    }

    private async Task<IReadOnlyList<Account>> FilterAccountsAsync(
        string pluginKey,
        string platformName,
        IReadOnlyList<Account> values,
        AdapterRequest request,
        TestOverrides? overrides,
        PluginAccountPolicy policy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = values
            .Where(present => string.Equals(present.Platform, platformName, StringComparison.OrdinalIgnoreCase)
                && present.Status.State is not (ResourceState.Disabled or ResourceState.Invalid)
                && policy.Selector?.Invoke(present) != false
                && policy.RequestSelector?.Invoke(present, request) != false
                && (overrides?.SkipCooldown == true || ResourceAvailability.IsAvailable(present, now)))
            .ToArray();
        if (candidates.Length == 0) return candidates;
        if (overrides?.SkipCooldown != true)
        {
            IReadOnlyList<DateTimeOffset?> available;
            try
            {
                available = shortTermState is RedisKeyValueStore redisState
                    ? await redisState.GetExpiryBatchAsync(
                        candidates.Select(present => AccountService.AccountCooldownKey(pluginKey, present.Id)).ToArray(), cancellationToken)
                    : await GetExpiriesBoundedAsync(candidates, pluginKey, cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                available = Enumerable.Repeat<DateTimeOffset?>(null, candidates.Length).ToArray();
            }
            candidates = candidates.Where((_, index) => available[index] is null || available[index] <= now).ToArray();
        }

        if (policy.BatchSelector is not null && candidates.Length > 0)
        {
            var selected = await policy.BatchSelector(candidates, request, cancellationToken);
            var byId = candidates.ToDictionary(account => account.Id, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selected is null || selected.Count > candidates.Length
                || selected.Any(item => item is null || !byId.ContainsKey(item.AccountId) || !seen.Add(item.AccountId)))
                throw new InvalidOperationException("Account selection returned duplicate or foreign account IDs.");
            return selected.Where(item => item.Eligible)
                .OrderBy(item => item.PreferredExpiry ?? DateTimeOffset.MaxValue).ThenByDescending(item => item.Weight)
                .ThenBy(_ => Random.Shared.Next()).Select(item => byId[item.AccountId]).ToArray();
        }
        if (policy.PreferredExpirySelector is null && policy.WeightSelector is null) return candidates;
        return candidates.OrderBy(present => policy.PreferredExpirySelector?.Invoke(present) ?? DateTimeOffset.MaxValue)
            .ThenByDescending(present => policy.WeightSelector?.Invoke(present, request) ?? 0)
            .ThenBy(_ => policy.WeightSelector is null && policy.PreferredExpirySelector is null ? 0 : Random.Shared.Next()).ToArray();
    }

    private async Task<IReadOnlyList<ProxyEndpoint>> GetProxiesAsync(
        string pluginKey,
        TestOverrides? overrides,
        CancellationToken cancellationToken)
    {
        var subscriptionsById = (await subscriptions.ListAsync(cancellationToken))
            .Where(item => item.Enabled)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var all = await proxies.ListAsync(cancellationToken);
        var forcedId = overrides?.ProxyId;
        var candidates = all
            .Where(proxy => (string.IsNullOrWhiteSpace(forcedId)
                || proxy.Id.Equals(forcedId, StringComparison.OrdinalIgnoreCase))
                && proxy.Status.State is ResourceState.Active
                && subscriptionsById.ContainsKey(proxy.SubscriptionId))
            .ToArray();
        if (candidates.Length == 0 || overrides?.SkipCooldown == true)
            return candidates;

        var now = DateTimeOffset.UtcNow;
        var states = proxyPolicy is RedisProxyPolicyStore redisPolicy
            ? await redisPolicy.GetManyAsync(pluginKey, candidates, cancellationToken)
            : await GetProxyPoliciesBoundedAsync(pluginKey, candidates, cancellationToken);
        return candidates.Where((_, index) =>
        {
            var state = states[index];
            return state?.State != ProxyPolicyState.Cooldown
                || state.CooldownUntil is not { } until
                || until <= now;
        }).ToArray();
    }

    private async Task<DateTimeOffset?[]> GetExpiriesBoundedAsync(
        Account[] candidates,
        string pluginKey,
        CancellationToken cancellationToken)
    {
        var expiries = new DateTimeOffset?[candidates.Length];
        for (var start = 0; start < candidates.Length; start += 16)
        {
            var count = Math.Min(16, candidates.Length - start);
            var batch = await Task.WhenAll(Enumerable.Range(start, count).Select(async index =>
            {
                try
                {
                    return await shortTermState.GetExpiryAsync(
                        AccountService.AccountCooldownKey(pluginKey, candidates[index].Id), cancellationToken);
                }
                catch when (!cancellationToken.IsCancellationRequested) { return null; }
            }));
            Array.Copy(batch, 0, expiries, start, count);
        }
        return expiries;
    }

    private async Task<ProxyPolicySnapshot?[]> GetProxyPoliciesBoundedAsync(
        string pluginKey,
        ProxyEndpoint[] candidates,
        CancellationToken cancellationToken)
    {
        var states = new ProxyPolicySnapshot?[candidates.Length];
        for (var start = 0; start < candidates.Length; start += 16)
        {
            var count = Math.Min(16, candidates.Length - start);
            var batch = await Task.WhenAll(Enumerable.Range(start, count).Select(index =>
                proxyPolicy.GetAsync(pluginKey, candidates[index], cancellationToken)));
            Array.Copy(batch, 0, states, start, count);
        }
        return states;
    }

    private static ProxyEndpoint? SelectProxy(IReadOnlyList<ProxyEndpoint> proxies, string? forcedId)
    {
        if (proxies.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(forcedId))
            return proxies.FirstOrDefault(proxy => proxy.Id.Equals(forcedId, StringComparison.OrdinalIgnoreCase));

        var sample = proxies.OrderBy(_ => Random.Shared.Next()).Take(10).ToArray();
        var freshQuality = sample
            .Where(proxy => proxy.ProbeStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                && proxy.ProbeConfigurationVersion == proxy.ConfigurationVersion
                && proxy.LastProbeAt is { } lastProbe
                && lastProbe >= DateTimeOffset.UtcNow.AddMinutes(-30))
            .ToArray();
        if (freshQuality.Length == 0)
            return sample[Random.Shared.Next(sample.Length)];

        return freshQuality
            .OrderByDescending(proxy => proxy.CompositeScore)
            .ThenBy(proxy => proxy.AverageLatencyMs)
            .ThenBy(_ => Random.Shared.Next())
            .First();
    }

    private static string FormatProxy(ProxyEndpoint proxy)
        => $"{proxy.Scheme.ToString().ToLowerInvariant()}://{proxy.Host}:{proxy.Port}";

    private async Task ApplyAccountDecisionAsync(
        string pluginKey,
        Account account,
        PluginAttemptResult result,
        CancellationToken cancellationToken)
    {
        var decision = result.Decision!;
        var reason = decision.AccountReason ?? result.Reason ?? decision.ReasonCode;
        if (decision.AccountAction == PluginAccountAction.Disable)
        {
            await accounts.DisableAsync(pluginKey, account.Id, reason, result.StatusCode, cancellationToken);
            return;
        }

        if (decision.AccountAction == PluginAccountAction.Cooldown)
        {
            await accounts.SetCooldownAsync(
                pluginKey,
                account.Id,
                decision.AccountCooldownUntil!.Value,
                reason,
                result.StatusCode,
                cancellationToken);
        }
    }

    private static PluginAttemptResult HostTransportFailure(PluginProxyPolicy policy, string reason, bool observed)
        => !observed
            ? new PluginAttemptDecision { FailureKind = PluginFailureKind.Plugin, ReasonCode = "plugin-execution-failure" }.ToResult(reason: reason)
            : policy.TransportFailureDecision?.Invoke().ToResult(reason: reason)
            ?? new PluginAttemptResult(PluginAttemptOutcome.CooldownNode, IsTransportFailure: true, Reason: reason);

    private async Task TryWriteLogAsync(PluginLog log)
    {
        try
        {
            await pluginLogs.WriteAsync(log, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "plugin detail log persistence failed pluginKey={PluginKey} event={EventType}",
                log.PluginKey,
                log.EventType);
        }
    }

    private static PluginInvocationResult Failure(string error, PluginAttemptOutcome outcome, string reasonCode)
        => new(
            AdapterResponse.ServerError(error),
            new PluginAttemptDecision
            {
                FailureKind = PluginFailureKind.Upstream,
                Retry = outcome == PluginAttemptOutcome.Retry ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
                ReasonCode = reasonCode
            }.ToResult(reason: error));
}
