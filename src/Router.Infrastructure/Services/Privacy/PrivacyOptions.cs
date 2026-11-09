using System.Text.Json;
using System.Text.RegularExpressions;

namespace Router.Infrastructure.Services.Privacy;

/// <summary>隐私护栏配置。默认关闭，开启后默认仅检测；不支持保存敏感原文。</summary>
public sealed class PrivacyOptions
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public string DefaultAction { get; set; } = "warn";
    public Dictionary<string, string> CategoryActions { get; set; } = new(StringComparer.Ordinal);
    public List<PrivacyCustomRule> CustomRules { get; set; } = [];
    /// <summary>入口插件 Key 或平台前缀；命中时跳过 block 规则，脱敏和告警仍执行。</summary>
    public List<string> WhitelistPlugins { get; set; } = [];

    /// <summary>请求快照拥有自己的配置副本。</summary>
    public PrivacyOptions Clone() => JsonSerializer.Deserialize<PrivacyOptions>(JsonSerializer.Serialize(this))!;
}

/// <summary>自定义正则；Action 为空时继承分类或全局动作。</summary>
public sealed record PrivacyCustomRule(string Id, string Name, string Pattern, bool Enabled = true, string? Action = null);

/// <summary>命中摘要只保存不可逆掩码，绝不保存原文。</summary>
public sealed record PrivacyHit(string RuleId, string Category, string Action, int Count, IReadOnlyList<string> Samples);

/// <summary>可存储的护栏结果；Error 只能是宿主定义的固定描述。</summary>
public sealed record PrivacyReport(bool DryRun, bool Blocked, int Redactions, IReadOnlyList<PrivacyHit> Hits, string? Error = null);

/// <summary>一次配置快照编译的规则。</summary>
public sealed record PrivacyRule(string Id, string Category, string Name, string Action, Regex Regex);

/// <summary>纯本地、有界的检测策略，原文仅在单请求检测器中短暂存在。</summary>
public sealed class PrivacyPolicy
{
    public static readonly string[] Actions = ["off", "warn", "block", "redact"];
    public static readonly string[] Categories =
        ["api_key", "aws_key", "jwt", "private_key", "email", "phone", "intranet", "credit_card", "national_id", "credential", "custom"];

    private static readonly (string Category, string Name, string Pattern)[] BuiltIns =
    [
        ("private_key", "PEM 私钥", @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----"),
        ("jwt", "JWT", @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"),
        ("api_key", "API Key / 访问令牌", @"\b(?:sk-(?:ant-)?[A-Za-z0-9_-]{16,}|AIza[A-Za-z0-9_-]{30,}|(?:ghp_|gho_|github_pat_|glpat-|gsk_|xai_|hf_)[A-Za-z0-9_-]{16,})"),
        ("aws_key", "AWS Access Key", @"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
        ("email", "邮箱地址", @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"),
        ("phone", "手机号", @"(?<!\d)(?:\+?86[- ]?)?1[3-9]\d{9}(?!\d)|(?<!\d)\+[1-9]\d{0,2}[- ](?:\d[- ]?){7,12}(?!\d)"),
        ("intranet", "内网地址", @"\b(?:10(?:\.\d{1,3}){3}|192\.168(?:\.\d{1,3}){2}|172\.(?:1[6-9]|2\d|3[01])(?:\.\d{1,3}){2}|[A-Za-z0-9][A-Za-z0-9.-]*\.(?:internal|local|lan|corp))\b"),
        ("credit_card", "银行卡号", @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)"),
        ("national_id", "身份证件", @"(?<!\d)[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)|(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)"),
        ("credential", "明文凭据", @"(?i)(?:password|passwd|api[_-]?key|access[_-]?token|secret)\s*[:=]\s*[""']?[^\s""',;]{6,}|(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis)://[^\s""']+")
    ];

    private PrivacyPolicy(PrivacyOptions options, PrivacyRule[] rules) { Options = options; Rules = rules; }
    public PrivacyOptions Options { get; }
    public IReadOnlyList<PrivacyRule> Rules { get; }

    /// <summary>配置写入前和启动时验证；禁止无界正则、重复规则及未知动作。</summary>
    public static PrivacyPolicy Build(PrivacyOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Actions.Contains(source.DefaultAction, StringComparer.Ordinal))
            throw new ArgumentException("隐私动作只能是 off、warn、block 或 redact。");
        if (source.CategoryActions is null || source.CustomRules is null
            || source.CustomRules.Count > 100
            || source.CategoryActions.Any(pair => !Categories.Contains(pair.Key, StringComparer.Ordinal)
                || !Actions.Contains(pair.Value, StringComparer.Ordinal)))
            throw new ArgumentException("隐私分类配置无效，或自定义规则超过 100 条。");
        if (source.WhitelistPlugins is null || source.WhitelistPlugins.Count > 50)
            throw new ArgumentException("插件白名单最多 50 项。");
        var whitelist = new List<string>(source.WhitelistPlugins.Count);
        var whitelistKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source.WhitelistPlugins)
        {
            var value = entry?.Trim() ?? "";
            if (value.Length is < 1 or > 128
                || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
                || !whitelistKeys.Add(value))
                throw new ArgumentException("插件白名单项须为 1–128 个英文字母、数字或 . _ -，且不能重复。");
            whitelist.Add(value);
        }
        var options = source.Clone();
        options.WhitelistPlugins = whitelist;
        var rules = BuiltIns.Select(rule => new PrivacyRule("builtin." + rule.Category, rule.Category, rule.Name,
            options.CategoryActions.GetValueOrDefault(rule.Category, options.DefaultAction),
            Compile(rule.Pattern, 500))).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in options.CustomRules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64
                || !rule.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                || !ids.Add(rule.Id) || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 80
                || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Length > 2048
                || rule.Action is not null && !Actions.Contains(rule.Action, StringComparer.Ordinal))
                throw new ArgumentException("自定义规则需要唯一 ID、名称、有效动作及最多 2048 字符的正则。");
            Regex compiled;
            try { compiled = Compile(rule.Pattern, 100); }
            catch (ArgumentException) { throw new ArgumentException($"自定义规则「{rule.Name}」的正则无效。"); }
            if (compiled.IsMatch(""))
                throw new ArgumentException($"自定义规则「{rule.Name}」不能匹配空字符串。");
            if (rule.Enabled)
                rules.Add(new(rule.Id, "custom", rule.Name,
                    rule.Action ?? options.CategoryActions.GetValueOrDefault("custom", options.DefaultAction), compiled));
        }
        return new(options, rules.Where(rule => rule.Action != "off").ToArray());
    }

    /// <summary>直接入口或真实回环转发来源命中白名单时，调用方可以跳过 block 规则。</summary>
    public bool IsWhitelisted(string? platform, string? pluginKey, string? forwardedPlugin = null)
        => Matches(platform) || Matches(pluginKey) || Matches(forwardedPlugin);

    private bool Matches(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && Options.WhitelistPlugins.Any(entry =>
                string.Equals(entry, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static Regex Compile(string pattern, int timeoutMs)
        => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(timeoutMs));
}
