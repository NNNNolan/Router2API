namespace Router.Infrastructure.Persistence;

/// <summary>SQLite 数据库配置。</summary>
public sealed class DatabaseOptions
{
    public string Path { get; set; } = "data/router2api.db";
}

/// <summary>Redis 运行态配置。节点策略没有内存事实回退。</summary>
public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";
    public string InstanceName { get; set; } = "router2api:";
}
