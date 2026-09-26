namespace Router.Contracts.Domain;

/// <summary>失败分类仅用于诊断；具体资源动作由独立字段表达。</summary>
public enum PluginFailureKind
{
    /// <summary>无失败。</summary>
    None,
    /// <summary>上游拒绝请求或返回业务错误。</summary>
    Upstream,
    /// <summary>凭据不可用。</summary>
    InvalidCredential,
    /// <summary>传输故障；宿主必须有实际 HTTP 故障佐证。</summary>
    Transport,
    /// <summary>插件自身执行、协议或输入错误，不惩罚代理。</summary>
    Plugin
}

/// <summary>业务尝试序列的重试动作，不会启用内层 HTTP 重试。</summary>
public enum PluginRetryAction
{
    /// <summary>结束本次调用。</summary>
    None,
    /// <summary>在剩余预算内重新选择资源；已返回流时宿主会拒绝此动作。</summary>
    NextAttempt
}

/// <summary>仅作用于本次宿主所选账号。</summary>
public enum PluginAccountAction
{
    /// <summary>不改变账号状态。</summary>
    None,
    /// <summary>冷却至 AccountCooldownUntil。</summary>
    Cooldown,
    /// <summary>停用账号。</summary>
    Disable
}

/// <summary>仅作用于本次实际使用且确有传输/代理认证故障的节点。</summary>
public enum PluginProxyAction
{
    /// <summary>不惩罚节点。</summary>
    None,
    /// <summary>按宿主节点策略冷却，插件不能指定节点或惩罚时长。</summary>
    Cooldown
}

/// <summary>插件的显式业务决策；宿主校验后只执行一次，不再重复应用旧 predicate。</summary>
public sealed record PluginAttemptDecision
{
    /// <summary>诊断分类。</summary>
    public PluginFailureKind FailureKind { get; init; }
    /// <summary>是否请求下一次业务尝试。</summary>
    public PluginRetryAction Retry { get; init; }
    /// <summary>账号动作。</summary>
    public PluginAccountAction AccountAction { get; init; }
    /// <summary>账号冷却截止时间，仅 Cooldown 动作可设置。</summary>
    public DateTimeOffset? AccountCooldownUntil { get; init; }
    /// <summary>可选的持久化账号原因，未指定时使用尝试原因。</summary>
    public string? AccountReason { get; init; }
    /// <summary>代理动作。</summary>
    public PluginProxyAction ProxyAction { get; init; }
    /// <summary>稳定的诊断代码；宿主不解析它来决定动作。</summary>
    public string ReasonCode { get; init; } = "none";

    /// <summary>验证决策形状和有界惩罚。此验证不代表传输故障已经被宿主证实。</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(FailureKind) || !Enum.IsDefined(Retry)
            || !Enum.IsDefined(AccountAction) || !Enum.IsDefined(ProxyAction)
            || string.IsNullOrWhiteSpace(ReasonCode) || ReasonCode.Length > 128
            || ReasonCode.Any(char.IsControl) || AccountReason?.Length > 2000)
            throw new InvalidOperationException("Invalid plugin attempt decision.");
        if ((AccountAction == PluginAccountAction.Cooldown) != AccountCooldownUntil.HasValue
            || AccountCooldownUntil > DateTimeOffset.UtcNow.AddDays(30))
            throw new InvalidOperationException("Account cooldown requires a deadline within 30 days.");
        if (FailureKind == PluginFailureKind.None
            && (Retry != PluginRetryAction.None || AccountAction != PluginAccountAction.None || ProxyAction != PluginProxyAction.None))
            throw new InvalidOperationException("A healthy decision cannot request retries or penalties.");
    }

    /// <summary>构造兼容旧日志/DLL 的尝试结果。宿主执行以 Decision 为准。</summary>
    /// <param name="statusCode">本次响应状态，可为空。</param>
    /// <param name="reason">面向操作者的说明，不参与动作判断。</param>
    /// <returns>包含显式决策及旧字段投影的结果。</returns>
    public PluginAttemptResult ToResult(int? statusCode = null, string? reason = null)
        => new(
            AccountAction == PluginAccountAction.Disable ? PluginAttemptOutcome.DisableAccount
            : ProxyAction == PluginProxyAction.Cooldown ? PluginAttemptOutcome.CooldownNode
            : AccountAction == PluginAccountAction.Cooldown ? PluginAttemptOutcome.CooldownAccount
            : Retry == PluginRetryAction.NextAttempt ? PluginAttemptOutcome.Retry
            : FailureKind == PluginFailureKind.None ? PluginAttemptOutcome.Healthy
            : PluginAttemptOutcome.NoPenalty,
            statusCode,
            IsTransportFailure: FailureKind == PluginFailureKind.Transport,
            IndicatesInvalidCredential: FailureKind == PluginFailureKind.InvalidCredential,
            Reason: reason) { Decision = this };
}
