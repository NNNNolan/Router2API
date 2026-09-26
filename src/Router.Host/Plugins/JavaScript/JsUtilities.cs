using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Router.Host.Plugins.JavaScript;

/// <summary>有界同步工具，无 CLR 对象、文件、网络或跨调用状态。</summary>
internal static class JsUtilities
{
    public static object? Execute(string operation, JsonObject input)
    {
        var text = input["text"]?.GetValue<string>() ?? "";
        return operation switch
        {
            "crypto.randomUUID" => Guid.NewGuid().ToString(),
            "crypto.hash" => Convert.ToHexString(Hash(input["algorithm"]?.GetValue<string>() ?? "SHA256", Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            "crypto.hmac" => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(input["key"]!.GetValue<string>()),
                Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            "encoding.base64Encode" => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            "encoding.base64Decode" => new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text)),
            "encoding.hexToBase64" => Convert.ToBase64String(Convert.FromHexString(text)),
            "decimal.add" => Decimal(input, (left, right) => left + right),
            "decimal.subtract" => Decimal(input, (left, right) => left - right),
            "decimal.multiply" => Decimal(input, (left, right) => left * right),
            "decimal.divide" => Decimal(input, (left, right) => left / right),
            "decimal.compare" => ReadDecimal(input, "left").CompareTo(ReadDecimal(input, "right")),
            "url.parse" => ParseUrl(text),
            "url.resolve" => new Uri(new Uri(input["base"]!.GetValue<string>(), UriKind.Absolute), text).AbsoluteUri,
            _ => throw new UnauthorizedAccessException("Unknown synchronous utility.")
        };
    }

    private static byte[] Hash(string algorithm, byte[] bytes) => algorithm.ToUpperInvariant() switch
    {
        "SHA256" => SHA256.HashData(bytes), "SHA384" => SHA384.HashData(bytes), "SHA512" => SHA512.HashData(bytes),
        _ => throw new InvalidOperationException("Supported hash algorithms: SHA256, SHA384, SHA512.")
    };
    private static string Decimal(JsonObject input, Func<decimal, decimal, decimal> operation)
        => operation(ReadDecimal(input, "left"), ReadDecimal(input, "right")).ToString(CultureInfo.InvariantCulture);
    private static decimal ReadDecimal(JsonObject input, string key)
        => decimal.Parse(input[key]!.GetValue<string>(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    private static object ParseUrl(string text)
    {
        var uri = new Uri(text, UriKind.Absolute);
        return new { href = uri.AbsoluteUri, origin = uri.GetLeftPart(UriPartial.Authority), scheme = uri.Scheme,
            host = uri.Host, port = uri.Port, path = uri.AbsolutePath, query = uri.Query, fragment = uri.Fragment };
    }
}
