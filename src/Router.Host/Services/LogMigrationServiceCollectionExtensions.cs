namespace Router.Host.Services;

public static class LogMigrationServiceCollectionExtensions
{
    public static IServiceCollection AddLogMigration(this IServiceCollection services)
    {
        services.AddSingleton<LogMigrationJobService>();
        services.AddHostedService(provider => provider.GetRequiredService<LogMigrationJobService>());
        return services;
    }
}
