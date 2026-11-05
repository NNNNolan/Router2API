using System.Text;
using System.Text.Json;

namespace Router.Infrastructure.Services.Privacy;

/// <summary>只延迟可能属于占位符的尾部；已替换的原文不会再被当作占位符递归替换。</summary>
public sealed class PlaceholderRestorer(IReadOnlyDictionary<string, string> map, bool jsonString = false)
{
    private string _pending = "";

    public string Push(string text)
    {
        if (map.Count == 0) return text;
        var source = _pending + text;
        _pending = "";
        var output = new StringBuilder();
        for (var offset = 0; offset < source.Length;)
        {
            if (source[offset] != '[') { output.Append(source[offset++]); continue; }
            var key = map.Keys.FirstOrDefault(key => source.AsSpan(offset).StartsWith(key, StringComparison.Ordinal));
            if (key is not null)
            {
                var original = map[key];
                output.Append(jsonString ? JsonEncodedText.Encode(original).ToString() : original);
                offset += key.Length;
                continue;
            }
            if (map.Keys.Any(key => key.AsSpan().StartsWith(source.AsSpan(offset), StringComparison.Ordinal)))
            {
                _pending = source[offset..];
                break;
            }
            output.Append(source[offset++]);
        }
        return output.ToString();
    }

    public string Flush() { var result = _pending; _pending = ""; return result; }

    /// <summary>完整字符串的单次非级联还原。</summary>
    public static string Restore(string text, IReadOnlyDictionary<string, string> map, bool jsonString = false)
    {
        var restorer = new PlaceholderRestorer(map, jsonString);
        return restorer.Push(text) + restorer.Flush();
    }
}
