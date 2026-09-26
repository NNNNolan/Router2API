using System.Net;
using Polly;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>独立选择代理并发送 HTTP，不参与账号、模型或业务状态码策略。</summary>
/// <param name="proxies">全局代理节点存储。</param>
/// <param name="subscriptions">代理订阅配置。</param>
/// <param name="transport">宿主管理的共享物理连接池。</param>
/// <param name="resilience">有限的共享传输策略。</param>
public sealed class ProxyPoolHttpClientFactory(
    IProxyStore proxies,
    IProxySubscriptionService subscriptions,
    ProxyTransportFactory transport,
    PluginResiliencePipelines resilience) : IProxyPoolHttpClientFactory
{
    private readonly IProxyStore _proxies = proxies ?? throw new ArgumentNullException(nameof(proxies));
    private readonly IProxySubscriptionService _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
    private readonly ProxyTransportFactory _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly PluginResiliencePipelines _resilience = resilience ?? throw new ArgumentNullException(nameof(resilience));

    /// <inheritdoc />
    public async Task<HttpClient> CreateClientAsync(
        ProxyPoolHttpClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProxyPoolHttpClientOptions();
        Validate(options, null);
        return await CreateClientCoreAsync(options, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        ProxyPoolHttpClientOptions? options = null,
        ProxyPoolRetryOptions? retry = null,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        options ??= new ProxyPoolHttpClientOptions();
        retry ??= new ProxyPoolRetryOptions();
        Validate(options, retry);
        var triedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new PluginResiliencePipelines.TransportRetryState(retry);

        async ValueTask<HttpResponseMessage> SendOnceAsync(CancellationToken token)
        {
            state.Sending = false;
            token.ThrowIfCancellationRequested();
            var client = await CreateClientCoreAsync(options, triedNodes, token);
            HttpRequestMessage? request = null;
            HttpResponseMessage? response = null;
            try
            {
                request = requestFactory() ?? throw new InvalidOperationException("The request factory returned null.");
                state.SafeMethod = request.Method == HttpMethod.Get
                    || request.Method == HttpMethod.Head
                    || request.Method == HttpMethod.Options;
                state.Sending = true;
                response = await client.SendAsync(request, completionOption, token);
                response.Content = new OwnedResponseContent(response.Content, client, request);
                return response;
            }
            catch
            {
                response?.Dispose();
                request?.Dispose();
                client.Dispose();
                throw;
            }
        }

        if (retry.MaxRetries == 0) return await SendOnceAsync(cancellationToken);

        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(PluginResiliencePipelines.TransportState, state);
        try
        {
            return await _resilience.Transport(retry).ExecuteAsync(
                ctx => SendOnceAsync(ctx.CancellationToken), context);
        }
        finally { ResilienceContextPool.Shared.Return(context); }
    }

    private async Task<HttpClient> CreateClientCoreAsync(
        ProxyPoolHttpClientOptions options,
        HashSet<string>? triedNodes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = options.SubscriptionIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = (await _subscriptions.ListAsync(cancellationToken))
            .Where(subscription => subscription.Enabled && (allowed is not { Count: > 0 } || allowed.Contains(subscription.Id)))
            .Select(subscription => subscription.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var candidates = (await _proxies.ListAsync(cancellationToken))
            .Where(proxy => enabled.Contains(proxy.SubscriptionId)
                && ResourceAvailability.IsAvailable(proxy, now)
                && !string.IsNullOrWhiteSpace(proxy.Host)
                && proxy.Port is > 0 and <= 65535)
            .ToArray();
        ProxyEndpoint? selected = null;
        if (candidates.Length > 0)
        {
            var remaining = candidates.Where(proxy => triedNodes?.Contains(proxy.Id) != true).ToArray();
            if (remaining.Length == 0)
            {
                triedNodes?.Clear();
                remaining = candidates;
            }
            selected = remaining[Random.Shared.Next(remaining.Length)];
            triedNodes?.Add(selected.Id);
        }
        else if (!options.AllowDirectFallback)
        {
            throw new ProxyPoolUnavailableException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _transport.CreateClient(selected, new PluginHttpClientOptions
        {
            AllowAutoRedirect = options.AllowAutoRedirect, RequestTimeout = options.RequestTimeout
        });
    }

    private static void Validate(ProxyPoolHttpClientOptions options, ProxyPoolRetryOptions? retry)
    {
        ProxyTransportFactory.ValidateTimeout(options.RequestTimeout);
        if (retry is not null
            && (retry.MaxRetries is < 0 or > 5 || retry.Delay < TimeSpan.Zero || retry.Delay > TimeSpan.FromSeconds(30)))
            throw new ArgumentOutOfRangeException(nameof(retry), "Retries must be 0–5 and delay must be 0–30 seconds.");
    }

    private sealed class OwnedResponseContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly HttpClient _client;
        private readonly HttpRequestMessage _request;
        private int _disposed;

        public OwnedResponseContent(HttpContent inner, HttpClient client, HttpRequestMessage request)
        {
            _inner = inner;
            _client = client;
            _request = request;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _inner.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => _inner.CopyToAsync(stream, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => _inner.ReadAsStreamAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    using (_client)
                    using (_request)
                        _inner.Dispose();
                }
            }
            finally { base.Dispose(disposing); }
        }
    }
}
