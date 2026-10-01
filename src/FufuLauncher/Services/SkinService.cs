// Copyright © FufuLauncher
//
// 玩家皮肤服务:为账号头像(玩家头颅)提供 64×64 皮肤纹理。
// 获取优先级:磁盘缓存(assets\skins\{uuid}.png)→ Mojang sessionserver 正版皮肤 → 失败/离线账号
// 时按 UUID 确定性随机生成(同一账号永远同一张脸,风格与游戏内皮肤贴图一致)。

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FufuLauncher.Services;

public static class SkinService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly ConcurrentDictionary<string, byte[]> _memCache = new(StringComparer.OrdinalIgnoreCase);

    private static string SkinDir => Path.Combine(AppPaths.Assets, "skins");

    static SkinService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FufuLauncher/3.0 (Windows)");
    }

    /// <summary>获取账号皮肤纹理 PNG 字节:磁盘缓存 → 官方接口 → UUID 随机生成,永不返回空</summary>
    public static async Task<byte[]> GetSkinBytesAsync(string uuid, AccountType type)
    {
        string key = string.IsNullOrEmpty(uuid) ? "default" : uuid;
        if (_memCache.TryGetValue(key, out var mem)) return mem;

        // 1) 磁盘缓存
        string diskFile = Path.Combine(SkinDir, $"{key}.png");
        try
        {
            if (File.Exists(diskFile))
            {
                var disk = await File.ReadAllBytesAsync(diskFile);
                if (disk.Length > 0) { Cache(key, disk); return disk; }
            }
        }
        catch { /* 缓存损坏走网络 */ }

        // 2) 微软正版账号:Mojang sessionserver 拉取真实皮肤
        if (type == AccountType.Microsoft && !string.IsNullOrEmpty(uuid))
        {
            var fetched = await TryFetchOfficialSkinAsync(uuid);
            if (fetched != null)
            {
                Cache(key, fetched);
                try
                {
                    Directory.CreateDirectory(SkinDir);
                    await File.WriteAllBytesAsync(diskFile, fetched);
                }
                catch { /* 写缓存失败不影响本次展示 */ }
                return fetched;
            }
            App.WriteAppLog($"[皮肤] 正版皮肤获取失败({uuid}),改用随机生成头像");
        }

        // 3) 离线/获取失败:按 UUID 确定性随机生成
        var generated = GenerateRandomSkin(key);
        Cache(key, generated);
        return generated;
    }

    private static void Cache(string key, byte[] bytes)
    {
        if (_memCache.Count < 200) _memCache.TryAdd(key, bytes);
    }

    // ==================== 本地皮肤文件(离线账号上传,Celestial-1) ====================

    /// <summary>磁盘皮肤缓存目录(assets\skins)</summary>
    public static string LocalSkinDir => SkinDir;

    /// <summary>该账号的本地皮肤文件完整路径</summary>
    public static string LocalSkinFile(string uuid) => Path.Combine(SkinDir, $"{uuid}.png");

    /// <summary>是否已存在用户自己上传/缓存的本地皮肤文件</summary>
    public static bool HasLocalSkin(string uuid) =>
        !string.IsNullOrEmpty(uuid) && File.Exists(LocalSkinFile(uuid));

    /// <summary>
    /// 写入本地皮肤文件并清掉内存缓存(下次取皮肤时会直接命中磁盘新文件)。
    /// 上传后必须清内存缓存,否则旧头像会一直糊在界面上。
    /// </summary>
    public static void WriteLocalSkin(string uuid, byte[] pngBytes)
    {
        if (string.IsNullOrEmpty(uuid) || pngBytes == null || pngBytes.Length == 0)
            throw new ArgumentException("皮肤数据为空");
        Directory.CreateDirectory(SkinDir);
        string file = LocalSkinFile(uuid);
        string tmp = file + ".tmp";
        File.WriteAllBytes(tmp, pngBytes);
        File.Move(tmp, file, overwrite: true);
        ClearCache(uuid);
        App.WriteAppLog($"[皮肤] 已写入本地皮肤 {file}({pngBytes.Length} 字节)");
    }

    /// <summary>删除本地皮肤文件并清内存缓存(回退到随机生成头像)</summary>
    public static bool RemoveLocalSkin(string uuid)
    {
        ClearCache(uuid);
        try
        {
            string file = LocalSkinFile(uuid);
            if (!File.Exists(file)) return false;
            File.Delete(file);
            App.WriteAppLog($"[皮肤] 已删除本地皮肤 {file}");
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[皮肤] 删除本地皮肤失败:{ex.Message}");
            return false;
        }
    }

    /// <summary>清除指定账号(传空 = 全部)的内存缓存</summary>
    public static void ClearCache(string? uuid = null)
    {
        if (string.IsNullOrEmpty(uuid)) _memCache.Clear();
        else _memCache.TryRemove(uuid, out _);
    }

    /// <summary>sessionserver 档案 → 皮肤 URL → 纹理字节;任一环节失败返回 null</summary>
    private static async Task<byte[]?> TryFetchOfficialSkinAsync(string uuid)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var profileUrl = $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}";
            var json = await _http.GetStringAsync(profileUrl, cts.Token);
            var root = JsonNode.Parse(json);
            if (root?["properties"] is not JsonArray props) return null;

            string? skinUrl = null;
            foreach (var prop in props)
            {
                if ((string?)prop?["name"] != "textures") continue;
                string? value = (string?)prop?["value"];
                if (string.IsNullOrEmpty(value)) continue;
                var decoded = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
                skinUrl = (string?)decoded?["textures"]?["SKIN"]?["url"];
                break;
            }
            if (string.IsNullOrEmpty(skinUrl)) return null;   // 无皮肤(默认 Steve/Alex),走随机生成保持个性

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bytes = await _http.GetByteArrayAsync(skinUrl, cts2.Token);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[皮肤] sessionserver 请求异常:{ex.Message}");
            return null;
        }
    }

    // ==================== 确定性随机皮肤生成 ====================
    // 布局遵循 64×64 标准皮肤贴图:头顶(8,0)-(15,7)、脸(8,8)-(15,15)、左右侧脸同规范,
    // 保证 SkinHeadKit 的等距头颅渲染与游戏内头颅模型一致。

    private static readonly uint[] SkinTones =
    {
        0xFFB98A6C, 0xFFC69B7B, 0xFFD9A886, 0xFFE8BE9C, 0xFFA9744F, 0xFF8D5B3C, 0xFFF0C8A0
    };
    private static readonly uint[] HairColors =
    {
        0xFF3B2A1A, 0xFF5C4033, 0xFF8B5A2B, 0xFF1F1B16, 0xFFA8763E, 0xFF6E4B2A, 0xFFD8C09A, 0xFF4A3B63
    };
    private static readonly uint[] EyeColors =
    {
        0xFF3D5CA8, 0xFF4E7A3A, 0xFF6B4423, 0xFF54406B, 0xFF2E6E6A
    };
    private static readonly uint[] ShirtColors =
    {
        0xFF3E6B4F, 0xFF4A5D8A, 0xFF8A4A4A, 0xFF5A4A8A, 0xFF2F6E75, 0xFF7A6A3A, 0xFF444C55
    };

    /// <summary>按 key(UUID)播种生成 64×64 皮肤 PNG(同 key 恒同脸)</summary>
    private static byte[] GenerateRandomSkin(string key)
    {
        var sha = SHA1.HashData(Encoding.UTF8.GetBytes("fufu-skin:" + key));
        var rnd = new Random(BitConverter.ToInt32(sha, 0));

        uint skinTone = SkinTones[rnd.Next(SkinTones.Length)];
        uint hair = HairColors[rnd.Next(HairColors.Length)];
        uint eye = EyeColors[rnd.Next(EyeColors.Length)];
        uint shirt = ShirtColors[rnd.Next(ShirtColors.Length)];
        uint skinShade = Shade(skinTone, 0.86);
        uint hairShade = Shade(hair, 0.8);

        var px = new uint[64 * 64];
        // 底衬(身体区域,头颅渲染不取,仅保证贴图完整)
        for (int i = 0; i < px.Length; i++) px[i] = shirt;

        void Rect(int x0, int y0, int w, int h, uint c)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++) px[y * 64 + x] = c;
        }

        // 头顶(8,0)-(15,7):发色带随机深浅噪点
        for (int y = 0; y < 8; y++)
            for (int x = 8; x < 16; x++)
                px[y * 64 + x] = rnd.NextDouble() < 0.22 ? hairShade : hair;

        // 四个侧面:肤色打底 + 顶部两行发际线
        foreach (var (sx, sy) in new[] { (0, 8), (8, 8), (16, 8), (24, 8) })
        {
            Rect(sx, sy, 8, 8, skinTone);
            for (int x = sx; x < sx + 8; x++)
            {
                px[sy * 64 + x] = hair;
                if (rnd.NextDouble() < 0.6) px[(sy + 1) * 64 + x] = hair;
            }
        }

        // 脸(8,8)-(15,15):眼睛(白底+瞳色)、眉毛、鼻子、嘴
        int ex1 = 9 + rnd.Next(2), ex2 = 14 - rnd.Next(2);      // 眼位小幅随机,避免呆板
        px[12 * 64 + ex1] = 0xFFFFFFFF; px[12 * 64 + ex1 + 1] = eye;
        px[12 * 64 + ex2 - 1] = eye;    px[12 * 64 + ex2] = 0xFFFFFFFF;
        px[11 * 64 + ex1] = hairShade;  px[11 * 64 + ex2] = hairShade;   // 眉毛
        px[13 * 64 + 11] = skinShade;   px[13 * 64 + 12] = skinShade;    // 鼻子
        px[14 * 64 + 11] = Shade(skinTone, 0.62); px[14 * 64 + 12] = Shade(skinTone, 0.62); // 嘴
        if (rnd.NextDouble() < 0.35)   // 概率腮红/痣,增强辨识度
            px[13 * 64 + (rnd.NextDouble() < 0.5 ? 9 : 14)] = Shade(skinTone, 0.9);

        // 帽子层(第二层)刘海:脸上一排半透发丝,让头颅更有立体层次
        for (int x = 40; x < 48; x++)
            if (rnd.NextDouble() < 0.55) px[8 * 64 + x] = hair;
        for (int x = 40; x < 48; x++)
            if (rnd.NextDouble() < 0.2) px[9 * 64 + x] = hair;

        // 编码 PNG
        var bmp = BitmapSource.Create(64, 64, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            ToBgraBytes(px), 64 * 4);
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] ToBgraBytes(uint[] px)
    {
        var bytes = new byte[px.Length * 4];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>颜色按系数压暗(保持 alpha)</summary>
    private static uint Shade(uint bgra, double f)
    {
        byte b = (byte)(bgra & 0xFF), g = (byte)((bgra >> 8) & 0xFF), r = (byte)((bgra >> 16) & 0xFF);
        return (0xFF000000u | ((uint)(r * f) << 16) | ((uint)(g * f) << 8) | (uint)(b * f));
    }
}
