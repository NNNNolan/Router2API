using System.Diagnostics;

namespace Router.Infrastructure.Services;

/// <summary>宿主阶段的单调时钟观测；不收集 HTTP 自动诊断、正文或凭据。</summary>
public sealed class RequestTrace : IDisposable
{
    private const string PropertyName = "Router2API.RequestTrace";
    private const int StageLimit = 1024;
    private readonly Activity _root = new("Router2API.request");
    private readonly Activity? _previous = Activity.Current;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly List<RequestTraceStage> _stages = [];
    private readonly object _gate = new();
    private bool _truncated;

    /// <summary>开始一个独立请求；关联 ID 可以重复，但此 Activity 和最终日志 ID 不会复用。</summary>
    public RequestTrace(string endpoint)
    {
        Endpoint = endpoint;
        _root.SetIdFormat(ActivityIdFormat.W3C);
        _root.SetCustomProperty(PropertyName, this);
        _root.Start();
    }

    /// <summary>调用方的协议入口，不包含查询参数或请求头。</summary>
    public string Endpoint { get; }

    /// <summary>结束流水线建立阶段的 ambient scope，不提前结束仍在消费的响应。</summary>
    public void Detach() => Activity.Current = _previous;

    /// <summary>在真实请求完成点冻结阶段观测。</summary>
    public RequestTraceData Complete()
    {
        var current = Activity.Current;
        _root.Stop();
        Activity.Current = current;
        lock (_gate)
            return new(Endpoint, _startedAt, Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                _truncated, _stages.OrderBy(stage => stage.StartMs).ToArray());
    }

    public void Dispose() => _root.Dispose();

    /// <summary>仅在宿主显式阶段创建观测，不把插件内部的 HTTP 链路纳入追踪。</summary>
    public static Stage? Measure(string name, string? pluginKey = null, string? platform = null, string? model = null)
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            if (activity.GetCustomProperty(PropertyName) is RequestTrace trace)
                return new Stage(trace, name, pluginKey, platform, model);
        return null;
    }

    /// <summary>一个有界的真实阶段观测；结果只能使用宿主定义的状态，不能写异常正文。</summary>
    public sealed class Stage : IDisposable
    {
        private readonly RequestTrace _trace;
        private readonly Activity _activity;
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly string? _pluginKey;
        private readonly string? _platform;
        private readonly string? _model;
        private string _outcome = "completed";
        private int? _statusCode;
        private bool _disposed;

        internal Stage(RequestTrace trace, string name, string? pluginKey, string? platform, string? model)
        {
            _trace = trace;
            _pluginKey = pluginKey;
            _platform = platform;
            _model = model;
            _activity = new Activity(name);
            _activity.SetCustomProperty(PropertyName, trace);
            _activity.Start();
        }

        /// <summary>登记结果，不记录凭据、网络地址或错误正文。</summary>
        public void SetResult(string outcome, int? statusCode = null)
        {
            _outcome = outcome;
            _statusCode = statusCode;
        }

        /// <summary>保存当前阶段并恢复父阶段。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var duration = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _activity.Stop();
            lock (_trace._gate)
            {
                if (_trace._stages.Count >= StageLimit) { _trace._truncated = true; return; }
                _trace._stages.Add(new(_activity.SpanId.ToHexString(), _activity.ParentSpanId.ToHexString(),
                    _activity.OperationName, Stopwatch.GetElapsedTime(_trace._started, _start).TotalMilliseconds,
                    duration, _outcome, _statusCode, _pluginKey, _platform, _model));
            }
        }
    }
}

/// <summary>可持久化的请求阶段摘要，不含任何请求或响应正文。</summary>
public sealed record RequestTraceData(string Endpoint, DateTimeOffset StartedAt, double DurationMs, bool Truncated,
    IReadOnlyList<RequestTraceStage> Stages);

/// <summary>阶段开始时间相对请求入口；父子关系来自 .NET Activity。</summary>
public sealed record RequestTraceStage(string Id, string ParentId, string Name, double StartMs, double DurationMs,
    string Outcome, int? StatusCode, string? PluginKey, string? Platform, string? Model);

/// <summary>只属于宿主的日志扩展，不改变插件 Contracts。</summary>
public sealed record RequestObservation(Privacy.PrivacyReport? Privacy, RequestTraceData Trace);
