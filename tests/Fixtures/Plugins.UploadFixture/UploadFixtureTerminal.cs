using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Plugins.UploadFixture;

/// <summary>用于上传测试的无网络、无副作用插件。</summary>
[PlatformAdapter("upload-fixture", PluginKey = "upload-fixture", DisplayName = "Upload fixture")]
public sealed class UploadFixtureTerminal(IPluginHost host) : IPlatformTerminal
{
    /// <summary>返回固定测试结果。</summary>
    public Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
        => Task.FromResult(new PluginInvocationResult(
            new AdapterResponse { StatusCode = 200 },
            new PluginAttemptDecision().ToResult(200)));

    /// <summary>返回空模型目录，不访问网络。</summary>
    public Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ModelDescriptor>>([]);

    /// <summary>验证宿主身份已传入。</summary>
    public Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken cancellationToken)
        => Task.FromResult(new CredentialValidationResult(host.PluginKey == "upload-fixture"));
}
