using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>宿主在插件请求第一次选择代理时绑定的节点和租约。</summary>
internal sealed class PluginProxyBinding(
    ProxyEndpoint proxy,
    HttpClient client,
    IAsyncDisposable lease) : IAsyncDisposable
{
    public ProxyEndpoint Proxy { get; } = proxy;
    public HttpClient Client { get; } = client;

    public async ValueTask DisposeAsync()
    {
        try { Client.Dispose(); }
        finally { await lease.DisposeAsync(); }
    }
}

/// <summary>
/// 插件看到的单一宿主客户端。代理池选择在请求级 SendAsync 时懒加载，直连请求不会触碰代理池。
/// </summary>
internal sealed class PluginHttpClient(
    Func<CancellationToken, Task<PluginProxyBinding?>> proxyFactory,
    HttpClient directClient,
    Action<HttpResponseMessage>? onResponse = null) : IPluginHttpClient, IAsyncDisposable
{
    private readonly SemaphoreSlim _proxyGate = new(1, 1);
    private PluginProxyBinding? _proxyBinding;
    private int _proxyInitialized;
    private int _usedProxy;
    private int _usedDirect;
    private int _transportFailure;
    private int _proxyFault;
    private int _disposed;
    private readonly object _responseGate = new();
    private readonly List<HttpResponseMessage> _responses = [];

    public ProxyEndpoint? UsedProxy
        => Volatile.Read(ref _usedProxy) == 1 ? _proxyBinding?.Proxy : null;

    public bool UsedDirect => Volatile.Read(ref _usedDirect) == 1;
    public bool ObservedTransportFailure => Volatile.Read(ref _transportFailure) == 1;
    public bool ObservedProxyFault => Volatile.Read(ref _proxyFault) == 1;

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
        => SendAsync(request, useProxyPool: true, completionOption, cancellationToken);

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool useProxyPool,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var client = directClient;
        var proxyUsed = false;
        if (useProxyPool)
        {
            var binding = await EnsureProxyAsync(cancellationToken);
            if (binding is not null)
            {
                Volatile.Write(ref _usedProxy, 1);
                client = binding.Client;
                proxyUsed = true;
            }
            else
            {
                Volatile.Write(ref _usedDirect, 1);
            }
        }
        else
        {
            Volatile.Write(ref _usedDirect, 1);
        }

        HttpResponseMessage response;
        try { response = await client.SendAsync(request, completionOption, cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            RecordTransportFailure(proxyUsed);
            throw;
        }
        if (proxyUsed && response.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired)
            Volatile.Write(ref _proxyFault, 1);
        response.Content = new ObservedHttpContent(response.Content, () => RecordTransportFailure(proxyUsed));
        lock (_responseGate)
        {
            if (_disposed != 0)
            {
                response.Dispose();
                throw new ObjectDisposedException(nameof(PluginHttpClient));
            }
            _responses.Add(response);
        }
        try { onResponse?.Invoke(response); }
        catch { response.Dispose(); throw; }
        return response;
    }

    private void RecordTransportFailure(bool proxyUsed)
    {
        Volatile.Write(ref _transportFailure, 1);
        if (proxyUsed) Volatile.Write(ref _proxyFault, 1);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        HttpResponseMessage[] responses;
        lock (_responseGate)
        {
            responses = _responses.ToArray();
            _responses.Clear();
        }
        List<Exception>? errors = null;
        foreach (var response in responses)
        {
            try { response.Dispose(); }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        var binding = Interlocked.Exchange(ref _proxyBinding, null);
        try { if (binding is not null) await binding.DisposeAsync(); }
        catch (Exception exception) { (errors ??= []).Add(exception); }
        try { directClient.Dispose(); }
        catch (Exception exception) { (errors ??= []).Add(exception); }
        finally { _proxyGate.Dispose(); }
        if (errors is not null) throw new AggregateException(errors);
    }

    private async Task<PluginProxyBinding?> EnsureProxyAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _proxyInitialized) == 1)
            return Volatile.Read(ref _proxyBinding);

        var current = Volatile.Read(ref _proxyBinding);
        if (current is not null) return current;

        await _proxyGate.WaitAsync(cancellationToken);
        try
        {
            current = _proxyBinding;
            if (current is null)
            {
                current = await proxyFactory(cancellationToken);
                _proxyBinding = current;
                Volatile.Write(ref _proxyInitialized, 1);
            }

            return current;
        }
        finally
        {
            _proxyGate.Release();
        }
    }
}
