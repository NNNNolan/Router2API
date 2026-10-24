namespace Router.Infrastructure.Services;

/// <summary>宿主进程内的映射目标健康状态；连续三轮请求失败后暂停五分钟。</summary>
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
            if (_states.TryGetValue(key, out var state) && state.CooldownUntil is { } until)
            {
                if (until > _clock.GetUtcNow())
                {
                    cooldownUntil = until;
                    return null;
                }
                // 新的一轮不接受上一冷却周期中仍未结束的请求结果。
                state = null;
            }
            if (state is null) _states[key] = state = new State();
            var admittedState = state;
            return new Invocation(success => Complete(key, admittedState, success));
        }
    }

    /// <summary>映射编辑后仅保留仍存在的目标状态，不因改名或调整顺序重置冷却。</summary>
    /// <param name="mapping">已持久化的映射。</param>
    public void RetainTargets(ModelMapping mapping)
    {
        var retained = mapping.Targets.Select(target => TargetKey.Create(mapping.Id, target)).ToHashSet();
        lock (_gate)
            foreach (var key in _states.Keys.Where(key => key.MappingId == mapping.Id && !retained.Contains(key)).ToArray())
                _states.Remove(key);
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
            if (++current.Failures < 3) return null;
            current.CooldownUntil = _clock.GetUtcNow().AddMinutes(5);
            return current.CooldownUntil;
        }
    }

    private sealed class State
    {
        public int Failures { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
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
