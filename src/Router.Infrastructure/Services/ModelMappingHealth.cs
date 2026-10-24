namespace Router.Infrastructure.Services;

/// <summary>宿主进程内的映射目标健康状态，按各目标配置的失败轮数和时长冷却。</summary>
/// <param name="timeProvider">用于冷却截止时间的时钟。</param>
public sealed class ModelMappingHealth(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<TargetKey, State> _states = [];

    /// <summary>开始一个目标的一轮请求；冷却期间返回空并给出截止时间。</summary>
    /// <param name="mappingId">稳定映射 ID。</param>
    /// <param name="target">插件、平台和模型目标。</param>
    /// <param name="cooldownUntil">尚未到期的冷却截止时间。</param>
    /// <returns>用于结算这一轮请求的句柄，或冷却时的空值。</returns>
    public Invocation? TryBegin(string mappingId, ModelMappingTarget target, out DateTimeOffset? cooldownUntil)
    {
        var key = TargetKey.Create(mappingId, target);
        lock (_gate)
        {
            cooldownUntil = null;
            _states.TryGetValue(key, out var state);
            // 规则保存后，在途请求仍可按旧快照执行，但不得改变新规则的健康状态。
            if (state is not null && !state.Matches(target)) return new Invocation(static _ => null);
            if (target.FailureThreshold == 0 || target.CooldownSeconds == 0)
            {
                return new Invocation(static _ => null);
            }
            if (state?.CooldownUntil is { } until)
            {
                if (until > _clock.GetUtcNow())
                {
                    cooldownUntil = until;
                    return null;
                }
                // 新的一轮不接受上一冷却周期中仍未结束的请求结果。
                state = null;
            }
            if (state is null) _states[key] = state = new State(target.FailureThreshold, target.CooldownSeconds);
            var admittedState = state;
            return new Invocation(success => Complete(key, admittedState, success));
        }
    }

    /// <summary>映射编辑后保留仍存在且规则不变的目标状态；规则变更会清空原状态。</summary>
    /// <param name="mapping">已持久化的映射。</param>
    public void RetainTargets(ModelMapping mapping)
    {
        var retained = mapping.Targets.ToDictionary(target => TargetKey.Create(mapping.Id, target));
        lock (_gate)
        {
            foreach (var pair in _states.Where(pair => pair.Key.MappingId == mapping.Id).ToArray())
                if (!retained.ContainsKey(pair.Key))
                    _states.Remove(pair.Key);
            foreach (var pair in retained)
                if (!_states.TryGetValue(pair.Key, out var state) || !state.Matches(pair.Value))
                    _states[pair.Key] = new State(pair.Value.FailureThreshold, pair.Value.CooldownSeconds);
        }
    }

    /// <summary>删除映射的运行态，并使其尚未完成的请求结果失效。</summary>
    /// <param name="mappingId">映射 ID。</param>
    public void Remove(string mappingId)
    {
        lock (_gate)
            foreach (var key in _states.Keys.Where(key => key.MappingId == mappingId).ToArray())
                _states.Remove(key);
    }

    private DateTimeOffset? Complete(TargetKey key, State admittedState, bool success)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var current) || !ReferenceEquals(current, admittedState)
                || current.CooldownUntil is not null) return null;
            if (success)
            {
                current.Failures = 0;
                return null;
            }
            if (++current.Failures < current.FailureThreshold) return null;
            current.CooldownUntil = _clock.GetUtcNow().AddSeconds(current.CooldownSeconds);
            return current.CooldownUntil;
        }
    }

    private sealed class State(int failureThreshold, int cooldownSeconds)
    {
        public int FailureThreshold { get; } = failureThreshold;
        public int CooldownSeconds { get; } = cooldownSeconds;
        public int Failures { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
        public bool Matches(ModelMappingTarget target)
            => FailureThreshold == target.FailureThreshold && CooldownSeconds == target.CooldownSeconds;
    }

    private readonly record struct TargetKey(string MappingId, string PluginKey, string Platform, string Model)
    {
        public static TargetKey Create(string mappingId, ModelMappingTarget target)
            => new(mappingId, target.PluginKey.ToUpperInvariant(), target.Platform.ToUpperInvariant(), target.Model.ToUpperInvariant());
    }

    /// <summary>一个映射目标的一轮请求；内部重试不重复结算。</summary>
    public sealed class Invocation
    {
        private readonly Func<bool, DateTimeOffset?> _complete;
        private int _completed;

        internal Invocation(Func<bool, DateTimeOffset?> complete) => _complete = complete;

        /// <summary>结算最终结果；成功清零，失败达到阈值时返回新冷却截止时间。</summary>
        /// <param name="success">整轮请求是否完成成功。</param>
        /// <returns>本次新触发的冷却截止时间；未触发或重复结算时为空。</returns>
        public DateTimeOffset? Complete(bool success)
            => Interlocked.Exchange(ref _completed, 1) == 0 ? _complete(success) : null;
    }
}
