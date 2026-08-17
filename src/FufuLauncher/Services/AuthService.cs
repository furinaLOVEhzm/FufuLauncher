// AuthService.cs — 账号登录服务
// 可爱的芙芙 - 微软账号 + 离线账号
//
// 微软登录流程(标准 OAuth 2.0 授权码模式):
//   1. 打开浏览器 → 用户登录微软账号
//   2. 微软页面显示授权码 → 用户复制粘贴回启动器
//   3. 用授权码换取 MS token → XBL → XSTS → MC token → 玩家资料
//
// 使用 Xbox 官方客户端 ID(00000000402b5328),该 ID 已被微软预授权 Minecraft API
// 端点使用 login.live.com (MSA 标准端点)
//
// 错误区分:AuthException 携带 AuthError 枚举
// 离线账号:仅保存昵称,生成确定性离线 UUID
// 令牌刷新:使用 refresh_token 自动重走完整令牌链

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public enum AccountType { Microsoft, Offline }

/// <summary>登录错误类型,供 UI 区分提示</summary>
public enum AuthError
{
    Network,              // 网络异常
    NotOwnedMinecraft,    // 账号未购买 Minecraft
    Cancelled,            // 用户取消
    TokenExchangeFailed,  // 令牌交换失败(MS/XBL/XSTS/MC 任一环节)
    Unknown
}

/// <summary>登录异常,携带错误类型供 UI 区分提示</summary>
public class AuthException : Exception
{
    public AuthError Error { get; }
    public AuthException(AuthError error, string message) : base(message) { Error = error; }
    public AuthException(AuthError error, string message, Exception inner) : base(message, inner) { Error = error; }
}

/// <summary>令牌基础混淆(XOR + Base64 + 魔数前缀):避免敏感令牌在配置文件中明文裸存。
/// 兼容旧版明文存档:解码失败时按原文返回,下次保存自动升级为混淆格式。</summary>
public static class TokenCipher
{
    private static readonly byte[] Key = "FufuLauncher#TokenCipher_v1"u8.ToArray();
    private const string Magic = "FU1|";

    public static string Obfuscate(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(Magic + plain);
        for (int i = 0; i < data.Length; i++) data[i] ^= Key[i % Key.Length];
        return Convert.ToBase64String(data);
    }

    public static string Deobfuscate(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            var data = Convert.FromBase64String(stored);
            for (int i = 0; i < data.Length; i++) data[i] ^= Key[i % Key.Length];
            string text = Encoding.UTF8.GetString(data);
            if (text.StartsWith(Magic, StringComparison.Ordinal)) return text[Magic.Length..];
            return stored; // 非本工具编码 → 视为旧版明文
        }
        catch { return stored; } // 旧版明文直接返回
    }
}

public class GameAccount
{
    public AccountType Type { get; set; }
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";

    // 令牌内存中明文使用,落盘时经 TokenCipher 混淆(不再明文裸存)
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccessToken { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string? MicrosoftRefreshToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("AccessToken")]
    public string AccessTokenStored
    {
        get => TokenCipher.Obfuscate(AccessToken);
        set => AccessToken = TokenCipher.Deobfuscate(value);
    }
    [System.Text.Json.Serialization.JsonPropertyName("MicrosoftRefreshToken")]
    public string? MicrosoftRefreshTokenStored
    {
        get => MicrosoftRefreshToken == null ? null : TokenCipher.Obfuscate(MicrosoftRefreshToken);
        set => MicrosoftRefreshToken = value == null ? null : TokenCipher.Deobfuscate(value);
    }

    public DateTime? TokenExpiresAt { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>列表控件(ComboBox/ListBox)默认显示文本,避免输出全命名空间类名</summary>
    public override string ToString() =>
        string.IsNullOrEmpty(Username) ? $"[{Type}]" : Username;
}

public class AuthService
{
    // OAuth 2.0 端点(Azure AD v2 consumers)
    private const string MsAuthorizeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    private const string MsTokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    // 注意:HttpClient 加 User-Agent / Accept 头。部分 Microsoft 端点
    // (api.minecraftservices.com)在没有 User-Agent 时会返回 401/403,
    // 导致令牌链路在最后一步失败。这里统一注入。
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0.5.1 (Windows)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>截断响应体用于日志(避免把整个长 JSON 写进日志)</summary>
    private static string TruncateBody(string s, int max = 800) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

    private readonly ConfigService _configService;
    private readonly string AccountsDir = AppPaths.Accounts; // 账号信息固化在 APP\mcGAME\accounts(与游戏数据完全隔离)

    private const string XboxAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McAuthUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    // 我们自己的 Azure 应用客户端 ID(登录链与游戏启动参数 ${clientid} 共用同一个,禁止分裂维护)
    public const string ClientId = "b9f4396e-4417-4604-a7a7-f7df5a298c5e";
    // 本地回调首选端口(Azure 已注册的回调 URI 端口;loopback 回调端口不参与匹配,可轮换)
    private const int CallbackPort = 54321;
    // 端口被占用时的轮换范围(依次尝试,不直接报错卡死)
    private static readonly int[] CallbackPortCandidates = { 54321, 54322, 54323, 54324, 54325, 54326, 54327, 54328 };
    // 本地回调 URI(已在 Azure 应用注册中添加)
    private const string RedirectUri = "http://127.0.0.1:54321/";
    // OAuth scope(v2 端点标准格式)
    private const string Scope = "XboxLive.signin offline_access";

    public List<GameAccount> Accounts { get; } = new();

    public AuthService(ConfigService configService)
    {
        _configService = configService;
    }

    public void LoadAccounts()
    {
        Accounts.Clear();
        Directory.CreateDirectory(AccountsDir);
        foreach (var file in Directory.EnumerateFiles(AccountsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var acc = JsonSerializer.Deserialize<GameAccount>(json);
                if (acc != null) Accounts.Add(acc);
            }
            catch { /* 忽略损坏账号文件 */ }
        }
    }

    public void SaveAccount(GameAccount account)
    {
        Directory.CreateDirectory(AccountsDir);
        string path = Path.Combine(AccountsDir, $"{account.Uuid}.json");
        var json = JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public void DeleteAccount(string uuid)
    {
        string path = Path.Combine(AccountsDir, $"{uuid}.json");
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* 文件锁容错 */ }
        }
        Accounts.RemoveAll(a => a.Uuid == uuid);
    }

    // ===== 本地回调登录(自动捕获授权码) =====

    /// <summary>
    /// 本地回调登录: 启动 HTTP 监听器 → 打开浏览器 → 等待回调 → 自动捕获授权码 → 换取 token
    /// 用户无需手动复制任何东西。
    /// </summary>
    public async Task<MsTokenResponse?> LoginWithLocalCallbackAsync(CancellationToken ct)
    {
        // 端口自动轮换:首选注册端口,被占用时依次尝试候选端口,不直接卡死报错
        var listener = new HttpListener();
        string redirectUri = RedirectUri;
        bool started = false;
        foreach (var port in CallbackPortCandidates)
        {
            redirectUri = $"http://127.0.0.1:{port}/";
            try
            {
                listener.Prefixes.Add(redirectUri);
                listener.Start();
                started = true;
                if (port != CallbackPort)
                    App.WriteAppLog($"[登录] 回调端口 {CallbackPort} 被占用,已自动轮换到 {port}");
                break;
            }
            catch (HttpListenerException)
            {
                // HttpListener 启动失败后不可复用,重建实例继续尝试下一端口
                try { listener.Close(); } catch { }
                listener = new HttpListener();
            }
        }
        if (!started)
        {
            throw new AuthException(AuthError.Unknown,
                $"无法监听任何回调端口({CallbackPortCandidates[0]}~{CallbackPortCandidates[^1]}),\n" +
                "请关闭占用端口的程序后重试。");
        }

        string state = Guid.NewGuid().ToString("N");
        string authUrl = $"{MsAuthorizeUrl}" +
            $"?client_id={ClientId}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&state={state}" +
            $"&prompt=login";

        App.WriteAppLog($"[登录] 打开浏览器授权页");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            listener.Stop();
            throw new AuthException(AuthError.Unknown, $"无法打开浏览器: {ex.Message}", ex);
        }

        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync().WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            listener.Stop();
            throw new AuthException(AuthError.Cancelled, "登录超时或已取消");
        }

        string? code = ctx.Request.QueryString["code"];
        string? returnedState = ctx.Request.QueryString["state"];
        string? error = ctx.Request.QueryString["error"];

        string responseHtml;
        // 页面骨架统一带 charset 声明 + 尝试自动关闭标签页(部分浏览器限制 JS 关闭,失败时用户手动关即可)
        const string Head = "<head><meta charset='utf-8'></head>";
        const string AutoClose = "<script>setTimeout(function(){try{window.close();}catch(e){}},800);</script>";
        if (!string.IsNullOrEmpty(code) && returnedState == state)
        {
            responseHtml = $"<html>{Head}<body style='font-family:sans-serif;text-align:center;padding:40px'>" +
                $"<h2>✅ 登录成功</h2><p>可爱的芙芙已收到登录凭证，请关闭此页面返回启动器。</p>{AutoClose}</body></html>";
        }
        else if (!string.IsNullOrEmpty(error))
        {
            string errDesc = ctx.Request.QueryString["error_description"] ?? error;
            responseHtml = $"<html>{Head}<body style='font-family:sans-serif;text-align:center;padding:40px'>" +
                $"<h2>❌ 登录失败</h2><p>{System.Net.WebUtility.HtmlEncode(errDesc)}</p></body></html>";
        }
        else
        {
            responseHtml = $"<html>{Head}<body style='font-family:sans-serif;text-align:center;padding:40px'>" +
                "<h2>❌ 登录失败</h2><p>未收到有效授权码，请重试。</p></body></html>";
        }
        
        byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
        // 必须显式声明 charset=utf-8:不设置时浏览器会按 GBK 猜解,中文页面全部乱码
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = buffer.Length;
        try { await ctx.Response.OutputStream.WriteAsync(buffer, ct); ctx.Response.Close(); }
        catch { /* 客户端断开忽略 */ }
        listener.Stop();

        if (!string.IsNullOrEmpty(error))
        {
            string errDesc = ctx.Request.QueryString["error_description"] ?? error;
            throw new AuthException(AuthError.Cancelled, $"登录被拒绝: {errDesc}");
        }
        if (string.IsNullOrEmpty(code) || returnedState != state)
            throw new AuthException(AuthError.Cancelled, "未收到有效授权码");

        App.WriteAppLog($"[登录] 收到授权码,长度={code.Length}");
        return await ExchangeAuthCodeAsync(code, redirectUri);
    }

    /// <summary>用授权码换取 MS token(redirect_uri 必须与授权时使用的回调地址一致)</summary>
    public async Task<MsTokenResponse?> ExchangeAuthCodeAsync(string authCode, string? redirectUri = null)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = authCode,
            ["redirect_uri"] = redirectUri ?? RedirectUri,
            ["scope"] = Scope
        });

        App.WriteAppLog($"[登录] 用授权码换取 MS token...");
        using var resp = await Http.PostAsync(MsTokenUrl, content);
        var json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            App.WriteAppLog($"[登录] 授权码换取 token 失败: StatusCode={resp.StatusCode}\n响应: {json}");
            return null;
        }

        var token = JsonSerializer.Deserialize<MsTokenResponse>(json);
        if (token == null || string.IsNullOrEmpty(token.AccessToken))
        {
            App.WriteAppLog($"[登录] token 响应解析失败。响应: {TruncateBody(json)}");
            return null;
        }
        App.WriteAppLog($"[登录] MS token 获取成功");
        return token;
    }

    // ===== 完整登录链(从 MS token 到 GameAccount)=====

    /// <summary>用 MS access_token 走完 XBL→XSTS→MC→资料 链,返回 GameAccount</summary>
    public async Task<GameAccount> CompleteLoginFromMsTokenAsync(MsTokenResponse msToken)
    {
        App.WriteAppLog("[登录] === 开始微软账号登录链路(XBL→XSTS→MC→Profile)===");

        // XBL 认证
        var xblToken = await AuthenticateXboxLiveAsync(msToken.AccessToken);
        if (xblToken == null || string.IsNullOrEmpty(xblToken.Token))
        {
            throw new AuthException(AuthError.TokenExchangeFailed,
                "Xbox Live 认证失败。\n详细信息请查看「设置 → 应用日志」。\n常见原因:\n" +
                "• 网络异常(请检查网络连接)\n" +
                "• 微软账号未创建 Xbox 档案(请先在 xbox.com 创建)\n" +
                "• 账号所在地区不支持 Xbox Live");
        }
        App.WriteAppLog($"[登录] ✓ XBL 认证成功(Token长度={xblToken.Token.Length},UserHash={xblToken.UserHash})");

        // XSTS 认证(解析常见错误码)
        var xsts = await AuthenticateXstsAsync(xblToken.Token);
        if (xsts == null || string.IsNullOrEmpty(xsts.Token))
        {
            throw new AuthException(AuthError.TokenExchangeFailed,
                "XSTS 令牌获取失败。\n详细信息请查看「设置 → 应用日志」。\n常见原因:\n" +
                "• 账号为儿童账号,需要家长同意(请到 family.microsoft.com 处理)\n" +
                "• Xbox 账号被封禁或限制\n" +
                "• 网络异常");
        }
        App.WriteAppLog($"[登录] ✓ XSTS 令牌获取成功(Token长度={xsts.Token.Length},UserHash={xsts.UserHash})");

        // Minecraft token
        var mcToken = await AuthenticateMinecraftAsync(xsts.Token, xsts.UserHash);
        if (mcToken == null || string.IsNullOrEmpty(mcToken.AccessToken))
        {
            App.WriteAppLog($"[登录] MC 令牌获取失败, XSTS UserHash={xsts.UserHash}, XSTS Token前50字符={xsts.Token.Substring(0, Math.Min(50, xsts.Token.Length))}...");
            throw new AuthException(AuthError.TokenExchangeFailed,
                "Minecraft 令牌获取失败。\n详细信息请查看「设置 → 应用日志」。\n常见原因:\n" +
                "• 网络异常(国内访问 api.minecraftservices.com 可能不稳定)\n" +
                "• XSTS 令牌格式不正确\n" +
                "• 微软服务器临时故障,请稍后重试");
        }
        App.WriteAppLog($"[登录] ✓ MC 令牌获取成功(AccessToken长度={mcToken.AccessToken.Length},ExpiresIn={mcToken.ExpiresIn}s)");

        // 获取 MC 账号信息(NotOwnedMinecraft 异常会直接抛出,由 UI 层捕获)
        var profile = await GetMinecraftProfileAsync(mcToken.AccessToken);
        // 资料为空视为登录失败:严禁用随机 UUID 建幽灵账号(会导致重复登录堆叠多个无效账号)
        if (profile == null || string.IsNullOrEmpty(profile.Id) || string.IsNullOrEmpty(profile.Name))
        {
            App.WriteAppLog("[登录] ✗ MC 玩家资料为空,登录中止");
            throw new AuthException(AuthError.Unknown,
                "获取 Minecraft 玩家资料失败(返回为空)。\n常见原因:\n" +
                "• 网络波动(国内访问 minecraftservices.com 不稳定),请稍后重试\n" +
                "• 微软服务器临时故障\n" +
                "• 账号状态异常,请到 minecraft.net 确认账号正常后重试");
        }
        App.WriteAppLog($"[登录] ✓ MC 玩家资料查询成功(玩家={profile.Name},UUID={profile.Id})");

        var account = new GameAccount
        {
            Type = AccountType.Microsoft,
            Username = profile.Name,
            Uuid = profile.Id,
            AccessToken = mcToken.AccessToken,
            MicrosoftRefreshToken = msToken.RefreshToken,
            TokenExpiresAt = DateTime.Now.AddSeconds(Math.Max(60, mcToken.ExpiresIn - 60))
        };
        SaveAccount(account);
        if (!Accounts.Exists(a => a.Uuid == account.Uuid)) Accounts.Add(account);
        App.WriteAppLog($"[登录] === 微软账号登录链路完成:玩家={account.Username},UUID={account.Uuid} ===");
        return account;
    }

    // ===== 离线账号(完整保留)=====

    /// <summary>离线账号登录</summary>
    public GameAccount LoginOffline(string nickname)
    {
        string uuid = GenerateOfflineUuid(nickname);
        var account = new GameAccount
        {
            Type = AccountType.Offline,
            Username = nickname,
            Uuid = uuid,
            AccessToken = uuid  // 离线 token 用 UUID 代替
        };
        SaveAccount(account);
        if (!Accounts.Exists(a => a.Uuid == account.Uuid)) Accounts.Add(account);
        return account;
    }

    // ===== Token 刷新(完整重走 XBL→XSTS→MC 链)=====

    /// <summary>使用 refresh_token 自动刷新微软令牌(完整重走令牌链)</summary>
    public async Task<bool> RefreshTokenAsync(GameAccount account)
    {
        if (account.Type != AccountType.Microsoft ||
            string.IsNullOrEmpty(account.MicrosoftRefreshToken))
        {
            return false;
        }

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = account.MicrosoftRefreshToken!,
                ["scope"] = Scope
            });
            using var resp = await Http.PostAsync(MsTokenUrl, content);
            if (!resp.IsSuccessStatusCode)
            {
                App.WriteAppLog($"[登录] 刷新 MS token 失败:{resp.StatusCode}");
                return false;
            }
            var json = await resp.Content.ReadAsStringAsync();
            var token = JsonSerializer.Deserialize<MsTokenResponse>(json);
            if (token == null || string.IsNullOrEmpty(token.AccessToken)) return false;

            // 完整重走 XBL → XSTS → MC 链
            var xblToken = await AuthenticateXboxLiveAsync(token.AccessToken);
            if (xblToken == null) return false;
            var xsts = await AuthenticateXstsAsync(xblToken.Token);
            if (xsts == null) return false;
            var mcToken = await AuthenticateMinecraftAsync(xsts.Token, xsts.UserHash);
            if (mcToken == null) return false;

            account.MicrosoftRefreshToken = token.RefreshToken;
            account.AccessToken = mcToken.AccessToken;
            account.TokenExpiresAt = DateTime.Now.AddSeconds(Math.Max(60, mcToken.ExpiresIn - 60));
            SaveAccount(account);
            App.WriteAppLog($"[登录] 账号 {account.Username} 令牌刷新成功");
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[登录] 刷新令牌异常:{ex.Message}");
            return false;
        }
    }

    public bool IsTokenExpired(GameAccount account) =>
        account.Type == AccountType.Microsoft &&
        account.TokenExpiresAt.HasValue &&
        account.TokenExpiresAt.Value <= DateTime.Now.AddMinutes(5);

    // ===== 内部方法 =====

    private static string GenerateOfflineUuid(string nickname)
    {
        // Minecraft 离线 UUID:基于 "OfflinePlayer:" + nickname 的 MD5 v3
        var bytes = Encoding.UTF8.GetBytes("OfflinePlayer:" + nickname);
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(bytes);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString("N");
    }

    private async Task<XblTokenResponse?> AuthenticateXboxLiveAsync(string msAccessToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msAccessToken}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("x-xbl-contract-version", "1");
        using var resp = await Http.PostAsync(XboxAuthUrl, content);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            App.WriteAppLog($"[登录] XBL 认证失败:{resp.StatusCode}\n响应:{TruncateBody(json)}");
            return null;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<XblTokenResponse>(json);
            if (parsed == null || string.IsNullOrEmpty(parsed.Token))
            {
                App.WriteAppLog($"[登录] XBL 响应缺少 Token 字段。响应:{TruncateBody(json)}");
                return null;
            }
            return parsed;
        }
        catch (JsonException ex)
        {
            App.WriteAppLog($"[登录] XBL 响应解析失败:{ex.Message}\n响应:{TruncateBody(json)}");
            return null;
        }
    }

    private async Task<XstsTokenResponse?> AuthenticateXstsAsync(string xblToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("x-xbl-contract-version", "1");
        using var resp = await Http.PostAsync(XstsUrl, content);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // 解析 XSTS 错误码(wiki.vg 公开文档)
            string xerr = "", xmsg = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("XErr", out var xe)) xerr = xe.ToString();
                if (doc.RootElement.TryGetProperty("Message", out var xm)) xmsg = xm.GetString() ?? "";
            }
            catch { /* 解析失败忽略 */ }

            string hint = xerr switch
            {
                "2148954107" => "账号为儿童账号,需要家长在 family.microsoft.com 同意",
                "2148954108" => "账号未创建 Xbox 档案,请先在 xbox.com 登录创建",
                "2148954109" => "Xbox Live 在该账号所在地区不可用",
                "2148954115" => "Xbox Live 服务暂时不可用,请稍后重试",
                _ => $"XSTS错误 {xerr}: {xmsg}"
            };
            App.WriteAppLog($"[登录] XSTS 认证失败:{resp.StatusCode} XErr={xerr}\n响应:{TruncateBody(json)}");
            App.WriteAppLog($"[登录] XSTS 错误解读:{hint}");
            return null;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<XstsTokenResponse>(json);
            if (parsed == null || string.IsNullOrEmpty(parsed.Token))
            {
                App.WriteAppLog($"[登录] XSTS 响应缺少 Token 字段。响应:{TruncateBody(json)}");
                return null;
            }
            return parsed;
        }
        catch (JsonException ex)
        {
            App.WriteAppLog($"[登录] XSTS 响应解析失败:{ex.Message}\n响应:{TruncateBody(json)}");
            return null;
        }
    }

    private async Task<McTokenResponse?> AuthenticateMinecraftAsync(string xstsToken, string userHash)
    {
        // 标准 MC 令牌请求(不需要 ensureLegacyEnabled,所有账号已迁移至微软)
        var body = JsonSerializer.Serialize(new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}"
        });
        App.WriteAppLog($"[登录] MC认证请求: identityToken前80字符=XBL3.0 x={userHash};{xstsToken.Substring(0, Math.Min(30, xstsToken.Length))}...");
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        // 国内网络不稳定,最多重试 3 次
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var resp = await Http.PostAsync(McAuthUrl, content);
                var json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    App.WriteAppLog($"[登录] MC 令牌获取失败(第{attempt}次): StatusCode={resp.StatusCode}\n完整响应: {json}");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(2000 * attempt); // 递增延迟重试
                        continue;
                    }
                    return null;
                }
                var parsed = JsonSerializer.Deserialize<McTokenResponse>(json);
                if (parsed == null || string.IsNullOrEmpty(parsed.AccessToken))
                {
                    App.WriteAppLog($"[登录] MC 响应缺少 access_token(第{attempt}次)。响应:{TruncateBody(json)}");
                    if (attempt < maxRetries) { await Task.Delay(2000 * attempt); continue; }
                    return null;
                }
                App.WriteAppLog($"[登录] MC 令牌获取成功(第{attempt}次尝试)");
                return parsed;
            }
            catch (HttpRequestException ex)
            {
                App.WriteAppLog($"[登录] MC 认证网络异常(第{attempt}次):{ex.Message}");
                if (attempt < maxRetries) { await Task.Delay(2000 * attempt); continue; }
                return null;
            }
            catch (JsonException ex)
            {
                App.WriteAppLog($"[登录] MC 响应解析失败(第{attempt}次):{ex.Message}");
                if (attempt < maxRetries) { await Task.Delay(2000 * attempt); continue; }
                return null;
            }
        }
        return null;
    }

    /// <summary>查询 Minecraft 玩家资料。404=未购买MC,其他错误按网络/未知处理</summary>
    private async Task<McProfileResponse?> GetMinecraftProfileAsync(string mcAccessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccessToken);
        try
        {
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // 404 表示该账号未拥有 Minecraft
                throw new AuthException(AuthError.NotOwnedMinecraft,
                    "该微软账号未购买 Minecraft Java 版,无法登录。\n请前往微软商店/Minecraft.net 购买后重试。");
            }
            if (!resp.IsSuccessStatusCode)
            {
                App.WriteAppLog($"[登录] 查询 MC 资料失败:{resp.StatusCode}\n响应:{TruncateBody(json)}");
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<McProfileResponse>(json);
            }
            catch (JsonException ex)
            {
                App.WriteAppLog($"[登录] MC 资料解析失败:{ex.Message}\n响应:{TruncateBody(json)}");
                return null;
            }
        }
        catch (AuthException) { throw; }
        catch (HttpRequestException ex)
        {
            App.WriteAppLog($"[登录] 查询玩家资料网络异常:{ex.GetType().Name}:{ex.Message}");
            throw new AuthException(AuthError.Network,
                "查询玩家资料时网络异常。\n请检查网络连接后重试,或尝试切换网络环境。", ex);
        }
    }

    // ===== 响应模型 =====
    public class MsTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
    public class XblTokenResponse
    {
        [JsonPropertyName("Token")] public string Token { get; set; } = "";
        [JsonPropertyName("DisplayClaims")] public XblDisplayClaims? DisplayClaims { get; set; }
        public string UserHash => DisplayClaims?.Xui?[0].UserHash ?? "";
    }
    public class XblDisplayClaims
    {
        [JsonPropertyName("xui")] public List<XblUserInfo>? Xui { get; set; }
    }
    public class XblUserInfo
    {
        [JsonPropertyName("uhs")] public string UserHash { get; set; } = "";
    }
    public class XstsTokenResponse
    {
        [JsonPropertyName("Token")] public string Token { get; set; } = "";
        [JsonPropertyName("DisplayClaims")] public XblDisplayClaims? DisplayClaims { get; set; }
        public string UserHash => DisplayClaims?.Xui?[0].UserHash ?? "";
    }
    public class McTokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
    public class McProfileResponse
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }
}
