using FluentAssertions;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class ModelRouterTests
{
    [TestMethod]
    public void PlatformPrefixIsNormalized()
    {
        var registry = new PlatformRegistry();
        registry.Register(new PlatformRegistration("myai", "myai", "MyAI", new TestTerminal()));
        var router = new ModelRouter(registry);

        router.TryResolve("myai/myai-chat", out var platform, out var normalized).Should().BeTrue();
        platform.Should().Be("myai");
        normalized.Should().Be("myai-chat");
        router.TryResolve("unknown/model", out _, out _).Should().BeFalse();
    }

    private sealed class TestTerminal : IPlatformTerminal
    {
        public Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
            => Task.FromResult(new PluginInvocationResult(
                AdapterResponse.ServerError("test terminal"),
                new PluginAttemptResult(PluginAttemptOutcome.NoPenalty, 500)));

        public Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelDescriptor>>([]);

        public Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken cancellationToken)
            => Task.FromResult(new CredentialValidationResult(true));
    }
}
