using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Router.Contracts.Host;

namespace Router.Host.Services;

/// <summary>宿主启动时预热模型及协议元数据，并定期更新。</summary>
public sealed class ModelMetadataWarmupHostedService(
    IModelMetadataCatalog catalog,
    ILogger<ModelMetadataWarmupHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await catalog.GetAsync(true, stoppingToken);
                logger.LogInformation(
                    "model metadata and {ProtocolCount} protocol mappings loaded",
                    snapshot.Protocols?.Count ?? 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Model metadata is advisory. The next scheduled or manual refresh can retry.
                logger.LogWarning(exception, "model metadata warmup failed; a retry is scheduled");
            }

            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }
}
