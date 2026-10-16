using Router.Contracts.Domain;

namespace Router.Infrastructure.Services;

/// <summary>保留调用方取消前已收集的尝试明细，同时维持取消异常语义。</summary>
public sealed class PluginRequestCancelledException(
    OperationCanceledException cause,
    IReadOnlyList<RequestAttemptDetail> attemptDetails,
    CancellationToken cancellationToken)
    : OperationCanceledException("request cancelled", cause, cancellationToken)
{
    public IReadOnlyList<RequestAttemptDetail> AttemptDetails { get; } = attemptDetails;
}
