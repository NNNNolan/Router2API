using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Router.Infrastructure.Services.Privacy;

/// <summary>每次请求的独立检测器。命中和时间预算耗尽均显式失败，不能解释成未命中。</summary>
public sealed class PrivacyInspection(PrivacyPolicy policy) : IDisposable
{
    private const int MatchLimit = 1024;
    private const int TextLimit = 16 * 1024 * 1024;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, string> _originals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Category, string Action, string Sample)>> _hits = new(StringComparer.Ordinal);
    private readonly string _prefix = "[R2A_" + Guid.NewGuid().ToString("N") + "_";
    private int _matches;
    private int _characters;
    private bool _blocked;
    private string? _error;

    /// <summary>还原映射只属于此请求，禁止序列化或放进日志。</summary>
    public IReadOnlyDictionary<string, string> RestoreMap => _originals;
    public bool DryRun => policy.Options.DryRun;
    public bool CanRedact => policy.Rules.Any(rule => rule.Action == "redact");
    public bool Blocked => _blocked && !DryRun;

    /// <summary>扫描一个已经过协议边界选择的文本，不扫描结构标识或二进制。</summary>
    public string Inspect(string text)
    {
        _characters += text.Length;
        if (_characters > TextLimit || _clock.Elapsed > TimeSpan.FromSeconds(5))
            return Fail("隐私检测超过请求预算。");
        var matches = new List<(int Start, int Length, PrivacyRule Rule)>();
        try
        {
            foreach (var rule in policy.Rules)
            {
                foreach (Match match in rule.Regex.Matches(text))
                {
                    if (match.Length == 0) return Fail("隐私规则匹配了空字符串。");
                    if (rule.Category == "credit_card" && !IsCard(match.Value)) continue;
                    if (++_matches > MatchLimit || _clock.Elapsed > TimeSpan.FromSeconds(5))
                        return Fail("隐私检测超过请求预算。");
                    _blocked |= rule.Action == "block";
                    if (!_hits.TryGetValue(rule.Id, out var hits)) _hits[rule.Id] = hits = [];
                    // 不保留任何原始字符；样本只描述长度，调试日志也不会成为敏感数据的第二份副本。
                    hits.Add((rule.Category, rule.Action, $"••••（{match.Length} 字符）"));
                    if (rule.Action == "redact")
                        matches.Add((match.Index, match.Length, rule));
                }
            }
        }
        catch (RegexMatchTimeoutException) { return Fail("隐私规则执行超时。"); }
        if (DryRun || matches.Count == 0) return text;
        // 重叠脱敏区间取并集；告警、阻断的检查仍基于同一份未修改文本。
        var ranges = new List<(int Start, int End)>();
        foreach (var match in matches.OrderBy(match => match.Start).ThenByDescending(match => match.Length))
        {
            if (ranges.Count == 0 || match.Start >= ranges[^1].End)
                ranges.Add((match.Start, match.Start + match.Length));
            else
                ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, match.Start + match.Length));
        }
        var result = new StringBuilder();
        var offset = 0;
        foreach (var range in ranges)
        {
            result.Append(text, offset, range.Start - offset);
            var original = text[range.Start..range.End];
            if (!_placeholders.TryGetValue(original, out var placeholder))
            {
                placeholder = _prefix + _originals.Count + "]";
                _placeholders.Add(original, placeholder);
                _originals.Add(placeholder, original);
            }
            result.Append(placeholder);
            offset = range.End;
        }
        return result.Append(text, offset, text.Length - offset).ToString();
    }

    /// <summary>持久化结果只含聚合和掩码；不会暴露还原映射。</summary>
    public PrivacyReport Report() => new(DryRun, Blocked, DryRun || Blocked ? 0 : _originals.Count,
        _hits.Select(pair => new PrivacyHit(pair.Key, pair.Value[0].Category, pair.Value[0].Action, pair.Value.Count,
            pair.Value.Select(hit => hit.Sample).Distinct(StringComparer.Ordinal).Take(3).ToArray())).ToArray(), _error);

    private string Fail(string message)
    {
        _error = message;
        throw new PrivacyInspectionException(message);
    }

    private static bool IsCard(string text)
    {
        var digits = text.Where(char.IsAsciiDigit).Select(character => character - '0').ToArray();
        if (digits.Length is < 13 or > 19 || digits.All(digit => digit == digits[0])) return false;
        // 13 位短卡号仅接受 Visa / Maestro 的常见前缀；Luhn 本身不能区分卡号与毫秒时间戳。
        var prefix = digits[0] * 10 + digits[1];
        if (digits.Length == 13 && prefix is not (>= 40 and <= 49 or 50 or >= 56 and <= 69)) return false;
        var sum = 0;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index];
            if ((digits.Length - 1 - index) % 2 == 1) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit;
        }
        return sum % 10 == 0;
    }

    /// <summary>在成功、失败和取消的共同释放点丢弃原文引用。</summary>
    public void Dispose() { _originals.Clear(); _placeholders.Clear(); }
}

/// <summary>只使用固定描述的护栏失败，不能包含待检测文本或正则匹配原文。</summary>
public sealed class PrivacyInspectionException(string message) : Exception(message);
