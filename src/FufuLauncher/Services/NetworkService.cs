// NetworkService.cs — 网络连通检测服务
// 可爱的芙芙 - 阶段1 模块
//
// 检测目标:
// 1. Mojang 官方源(piston-meta.mojang.com / launchermeta.mojang.com)
// 2. BMCLAPI 国内镜像源(bmclapi2.bangbang93.com)
// 返回连通状态与延迟

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

public record ConnectivityResult(bool MojangOk, bool BmclapiOk, string Message);

public class NetworkService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    static NetworkService()
    {
        // 携带 User-Agent 避免被 Mojang / BMCLAPI 限流
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/1.0.5.1 (Windows)");
    }

    public const string MojangMetaUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string BmclapiMetaUrl = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

    public async Task<ConnectivityResult> TestConnectivityAsync()
    {
        long mojangDelay = -1, bmclapiDelay = -1;
        bool mojangOk = false, bmclapiOk = false;
        string msg;

        try
        {
            (mojangOk, mojangDelay) = await TestEndpointAsync(MojangMetaUrl);
        }
        catch
        {
            mojangOk = false;
        }

        try
        {
            (bmclapiOk, bmclapiDelay) = await TestEndpointAsync(BmclapiMetaUrl);
        }
        catch
        {
            bmclapiOk = false;
        }

        if (mojangOk && bmclapiOk)
        {
            msg = $"官方源 {mojangDelay}ms · BMCLAPI {bmclapiDelay}ms · 双源可用";
        }
        else if (bmclapiOk)
        {
            msg = $"BMCLAPI 可用({bmclapiDelay}ms),官方源不可用";
        }
        else if (mojangOk)
        {
            msg = $"官方源可用({mojangDelay}ms),BMCLAPI 不可用";
        }
        else
        {
            msg = "Mojang 官方源与 BMCLAPI 国内镜像源均不可用,请检查网络";
        }

        return new ConnectivityResult(mojangOk, bmclapiOk, msg);
    }

    private async Task<(bool ok, long ms)> TestEndpointAsync(string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await Http.GetAsync(url);
            sw.Stop();
            bool ok = resp.IsSuccessStatusCode;
            return (ok, sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds);
        }
    }
}
