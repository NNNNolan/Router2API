using System.Text.Json;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>一个对外模型名及按调用优先级排列的插件目标。</summary>
public sealed record ModelMapping(string Id, string Name, bool Enabled, IReadOnlyList<ModelMappingTarget> Targets);

/// <summary>映射目标；RetryCount 沿用原字段名，表示包含首次请求的总尝试次数，空值继承插件策略。</summary>
public sealed record ModelMappingTarget(string PluginKey, string Platform, string Model, bool Enabled = true, int? RetryCount = null);

/// <summary>将整条映射原子保存为一行，数组顺序即调用顺序；每次请求读取独立快照。</summary>
public sealed class ModelMappingStore(SqlSugarDatabase database, ModelMappingHealth health)
{
    private readonly object _writeGate = new();

    /// <summary>读取所有映射，包括停用的映射。</summary>
    public IReadOnlyList<ModelMapping> List()
    {
        using var db = database.CreateClient();
        return db.Queryable<ModelMappingEntity>().OrderBy(row => row.Name).ToList().Select(Decode).ToArray();
    }

    /// <summary>按不区分大小写的对外模型名读取映射。</summary>
    public ModelMapping? Find(string name)
    {
        var key = name.ToUpperInvariant();
        using var db = database.CreateClient();
        var row = db.Queryable<ModelMappingEntity>().First(row => row.NameKey == key);
        return row is null ? null : Decode(row);
    }

    /// <summary>创建或更新映射；更新必须指定已存在的 ID。</summary>
    /// <param name="mapping">完整映射；新建时 ID 留空。</param>
    /// <returns>包含稳定 ID 的持久化快照。</returns>
    public ModelMapping Save(ModelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var name = mapping.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 128 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("对外模型名须为 1–128 个英文字母、数字或 . _ - :，不能包含 /。");
        if (mapping.Targets is null || mapping.Targets.Count is < 1 or > 32)
            throw new ArgumentException("每个映射须包含 1–32 个目标。");
        var targets = mapping.Targets.Select(target =>
        {
            if (target is null || !ValidIdentifier(target.PluginKey, 128) || !ValidIdentifier(target.Platform, 128)
                || !ValidIdentifier(target.Model, 256) || target.RetryCount is < 0 or > 35)
                throw new ArgumentException("目标须填写插件、平台和模型；尝试次数须为 1–35（包含首次请求）或留空继承。");
            return NormalizeLegacyCount(target with { PluginKey = target.PluginKey.Trim(), Platform = target.Platform.Trim(), Model = target.Model.Trim() });
        }).ToArray();
        if (targets.Select(target => (target.PluginKey.ToUpperInvariant(), target.Platform.ToUpperInvariant(), target.Model.ToUpperInvariant())).Distinct().Count() != targets.Length)
            throw new ArgumentException("同一个插件、平台和模型不能重复添加。");
        if (mapping.Enabled && !targets.Any(target => target.Enabled))
            throw new ArgumentException("启用的映射至少需要一个启用的目标。");
        var creating = string.IsNullOrEmpty(mapping.Id);
        if (!creating && !Guid.TryParseExact(mapping.Id, "N", out _))
            throw new ArgumentException("映射 ID 无效。");

        lock (_writeGate)
        {
            using var db = database.CreateClient();
            var key = name.ToUpperInvariant();
            if (db.Queryable<ModelMappingEntity>().Any(row => row.NameKey == key && row.Id != mapping.Id))
                throw new InvalidOperationException("对外模型名已存在。");
            var saved = new ModelMapping(creating ? Guid.NewGuid().ToString("N") : mapping.Id, name, mapping.Enabled, targets);
            var row = new ModelMappingEntity
            {
                Id = saved.Id, Name = saved.Name, NameKey = key, Enabled = saved.Enabled,
                TargetsJson = JsonSerializer.Serialize(targets)
            };
            if (creating) db.Insertable(row).ExecuteCommand();
            else if (db.Updateable(row).ExecuteCommand() == 0) throw new KeyNotFoundException("映射不存在。");
            health.RetainTargets(saved);
            return saved;
        }
    }

    /// <summary>删除指定映射，返回是否存在该映射。</summary>
    public bool Delete(string id)
    {
        using var db = database.CreateClient();
        var deleted = db.Deleteable<ModelMappingEntity>().Where(row => row.Id == id).ExecuteCommand() > 0;
        if (deleted) health.Remove(id);
        return deleted;
    }

    private static bool ValidIdentifier(string? value, int limit)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static ModelMapping Decode(ModelMappingEntity row)
        => new(row.Id, row.Name, row.Enabled, (JsonSerializer.Deserialize<ModelMappingTarget[]>(row.TargetsJson)
            ?? throw new InvalidOperationException($"Model mapping '{row.Name}' has invalid targets."))
            .Select(NormalizeLegacyCount).ToArray());

    // 旧配置的零次额外重试仍表示一次调用；正数直接作为总尝试次数。
    private static ModelMappingTarget NormalizeLegacyCount(ModelMappingTarget target)
        => target.RetryCount == 0 ? target with { RetryCount = 1 } : target;
}
