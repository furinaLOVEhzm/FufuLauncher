// SensitiveData.cs — 敏感信息脱敏工具(安全审计修复,2026-08-28)
// FufuLauncher
//
// 用途:日志、弹窗等对外输出场景统一经此脱敏,防止代理凭据、令牌等
// 敏感信息以明文形式落入 日志\app.log 或显示给用户截图外传。

namespace FufuLauncher.Services;

public static class SensitiveData
{
    /// <summary>
    /// URL 脱敏:剥离内嵌凭据段。
    /// "http://user:pass@host:7890" → "http://***@host:7890"
    /// 无凭据的 URL 原样返回;解析失败仅遮蔽 @ 前缀部分,绝不输出完整原文。
    /// </summary>
    public static string MaskUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                return url.Replace(uri.UserInfo + "@", "***@");
            }
            return url;
        }
        catch
        {
            // 非法 URL:若含 @ 凭据段则遮蔽,防止意外明文泄露
            int at = url.LastIndexOf('@');
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (at > scheme + 3) return url[..(scheme + 3)] + "***@" + url[(at + 1)..];
            return url;
        }
    }

    /// <summary>令牌/密钥脱敏:仅保留首 4 尾 2 字符,其余以 *** 代替;过短全遮</summary>
    public static string MaskSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        return secret.Length <= 6 ? "***" : $"{secret[..4]}***{secret[^2..]}";
    }
}
