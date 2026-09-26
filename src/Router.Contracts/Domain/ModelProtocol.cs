namespace Router.Contracts.Domain;

/// <summary>模型上游协议类型。</summary>
public enum ModelProtocol
{
    /// <summary>OpenAI Chat Completions 协议。</summary>
    ChatCompletions,

    /// <summary>OpenAI Responses 协议。</summary>
    Responses,

    /// <summary>Anthropic Messages 协议。</summary>
    AnthropicMessages
}

/// <summary>模型协议相关的公共辅助方法。</summary>
public static class ModelProtocolExtensions
{
    /// <summary>获取协议对应的标准路径片段。</summary>
    /// <param name="protocol">模型协议。</param>
    /// <returns>不包含版本前缀的路径片段。</returns>
    public static string GetEndpointPath(this ModelProtocol protocol)
        => protocol switch
        {
            ModelProtocol.Responses => "responses",
            ModelProtocol.AnthropicMessages => "messages",
            _ => "chat/completions"
        };
}

/// <summary>按工具调用索引聚合流式增量。</summary>
public sealed class ToolCallAccumulator
{
    private readonly System.Text.StringBuilder _arguments = new();

    /// <summary>获取已聚合的工具调用 ID。</summary>
    public string? Id { get; private set; }

    /// <summary>获取已聚合的工具调用类型。</summary>
    public string? Type { get; private set; }

    /// <summary>获取已聚合的函数名称。</summary>
    public string? Name { get; private set; }

    /// <summary>获取已聚合的 JSON 参数文本。</summary>
    public string Arguments => _arguments.ToString();

    /// <summary>应用一个工具调用增量。</summary>
    /// <param name="delta">工具调用增量。</param>
    public void Apply(ToolCallDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        Id ??= delta.Id;
        Type ??= delta.Type;
        Name ??= delta.Name;
        if (!string.IsNullOrEmpty(delta.Arguments))
            _arguments.Append(delta.Arguments);
    }

    /// <summary>将当前聚合结果转换为统一工具调用增量。</summary>
    /// <param name="index">工具调用索引。</param>
    /// <returns>可用于统一响应的工具调用信息。</returns>
    public ToolCallDelta ToDelta(int index)
        => new(index, Id, Type, Name, Arguments);
}
