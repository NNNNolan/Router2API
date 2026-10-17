using Router.Contracts.Domain;
using Router.Contracts.Plugins;

namespace Router.Infrastructure.Services;

/// <summary>唯一的旧契约适配/决策校验位置；完成后执行器不再解释 reason 或 predicate。</summary>
internal static class PluginAttemptDecisions
{
    public static PluginAttemptResult Resolve(PluginInvocationResult result, PluginProxyPolicy policy,
        PluginAccountPolicy accountPolicy, bool transportFailure, bool proxyFault)
    {
        var attempt = result.Attempt;
        if (!Enum.IsDefined(attempt.Outcome)) throw new InvalidOperationException("Unknown plugin attempt outcome.");
        attempt.Decision?.Validate();
        if (result.Response.IsSuccess)
            return new PluginAttemptDecision { ReasonCode = "success" }.ToResult(attempt.StatusCode, attempt.Reason);
        // 不能把脚本或 DLL 自报的标记当作宿主传输失败的证据。
        attempt = attempt with { IsTransportFailure = attempt.IsTransportFailure && transportFailure };
        var decision = attempt.Decision ?? MapLegacy(attempt, policy, accountPolicy);
        decision.Validate();
        if (decision.FailureKind == PluginFailureKind.Transport && !transportFailure)
            decision = decision with { FailureKind = PluginFailureKind.Plugin };
        if (!proxyFault)
            decision = decision with { ProxyAction = PluginProxyAction.None };
        // 资源所有权已移交消费端，不能丢弃或重放已经返回的流。
        if (PluginResponseLifetime.HasStream(result.Response))
            decision = decision with { Retry = PluginRetryAction.None };
        return decision.ToResult(attempt.StatusCode, attempt.Reason);
    }

    private static PluginAttemptDecision MapLegacy(PluginAttemptResult result, PluginProxyPolicy policy, PluginAccountPolicy account)
    {
        var disable = account.DisablePredicate?.Invoke(result) ?? policy.DisableAccountPredicate?.Invoke(result) ?? false;
        var cooldown = !disable && (account.CooldownPredicate?.Invoke(result) ?? policy.CooldownAccountPredicate?.Invoke(result) ?? false);
        return new PluginAttemptDecision
        {
            FailureKind = result.IsTransportFailure ? PluginFailureKind.Transport
                : result.IndicatesInvalidCredential ? PluginFailureKind.InvalidCredential
                : PluginFailureKind.Upstream,
            Retry = policy.RetryPredicate?.Invoke(result) == true ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
            AccountAction = disable ? PluginAccountAction.Disable : cooldown ? PluginAccountAction.Cooldown : PluginAccountAction.None,
            AccountCooldownUntil = cooldown ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
            ProxyAction = result.Outcome == PluginAttemptOutcome.CooldownNode || policy.CooldownNodePredicate?.Invoke(result) == true
                ? PluginProxyAction.Cooldown : PluginProxyAction.None,
            ReasonCode = "legacy"
        };
    }
}
