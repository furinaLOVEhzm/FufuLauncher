// JvmArgsValidator.cs — JVM 自定义参数语法校验
// 可爱的芙芙
//
// 高级模式自定义 JVM 参数的语法校验(保存前调用):
// - 支持双引号包裹的参数值(引号必须配对)
// - 每个参数必须以 - 开头(或 ${...} 占位符)
// - -Xms/-Xmx 格式校验(如 4096m / 4g)
// - 禁止与启动器托管冲突的参数(-cp/-classpath/-jar/-Xms/-Xmx 由启动器统一管理)

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FufuLauncher.Services;

public static class JvmArgsValidator
{
    private static readonly Regex MemArgRegex = new(@"^-X(ms|mx|ss)\d+[kKmMgG]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>校验自定义 JVM 参数字符串。返回 (是否合法, 错误信息)</summary>
    public static (bool Ok, string? Error) Validate(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return (true, null);

        // 1) 引号配对检查
        int quoteCount = 0;
        foreach (char c in args) if (c == '"') quoteCount++;
        if (quoteCount % 2 != 0)
            return (false, "引号不配对:请检查双引号是否成对");

        // 2) 按空白切分(尊重双引号)
        var tokens = Tokenize(args);
        if (tokens.Count == 0) return (true, null);

        foreach (var token in tokens)
        {
            string t = token.Trim('"');
            if (t.Length == 0)
                return (false, "存在空参数(连续的引号或空白)");

            // 参数必须以 - 开头或为占位符
            if (!t.StartsWith('-') && !t.StartsWith("${"))
                return (false, $"非法参数「{Trunc(t)}」:JVM 参数必须以 - 开头");

            // 禁止与启动器托管冲突的参数
            string lower = t.ToLowerInvariant();
            if (lower is "-cp" or "-classpath" or "--class-path" or "-jar" or "--module" or "-m" or "-p" or "--module-path")
                return (false, $"参数「{Trunc(t)}」由启动器统一管理,不允许手动指定");
            if (lower.StartsWith("-xms") || lower.StartsWith("-xmx"))
                return (false, $"内存参数「{Trunc(t)}」请使用设置页的内存分配功能统一配置");

            // 内存类参数格式校验
            if (lower.StartsWith("-xms") || lower.StartsWith("-xmx") || lower.StartsWith("-xss"))
            {
                if (!MemArgRegex.IsMatch(t))
                    return (false, $"内存参数「{Trunc(t)}」格式错误:应为 -Xmx4096m / -Xmx4g 等");
            }

            // -XX 参数格式校验
            if (t.StartsWith("-XX:"))
            {
                string body = t[4..];
                // 合法形式:+Flag / -Flag / Flag=value / Flag:=value
                if (!(body.StartsWith('+') || body.StartsWith('-') || body.Contains('=')))
                    return (false, $"参数「{Trunc(t)}」格式错误:-XX: 后应为 +开关、-开关 或 键=值");
            }

            // -D 系统属性格式校验
            if (t.StartsWith("-D") && t.Length > 2 && !t[2..].Contains('='))
                return (false, $"参数「{Trunc(t)}」格式错误:-D 属性应为 -Dkey=value");
        }

        return (true, null);
    }

    private static string Trunc(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>按空白切分参数,双引号内的空白视为整体</summary>
    private static List<string> Tokenize(string args)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (char c in args)
        {
            if (c == '"') { inQuote = !inQuote; cur.Append(c); continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
