// Copyright © FufuLauncher
//
// 离线账号皮肤服务(Celestial-1 的"上传本地皮肤文件,应用给离线账号"):
// 用户从磁盘挑一张皮肤图 → 校验是不是合法的 Minecraft 皮肤贴图 → 规整成 64×64 PNG
// → 写进 assets\skins\{uuid}.png → 清掉内存缓存,界面头像立即生效。
//
// 实现要点:
// 1. 支持 64×64(现代)与 64×32(1.8 之前的老皮肤)两种规格,老皮肤自动补下半身(镜像手臂/腿);
// 2. 尺寸不对、不是 PNG、文件过大一律拒绝并给出中文明确原因,绝不静默生成花屏;
// 3. 非 PNG 格式(bmp/jpg)自动重编码成 PNG,统一走磁盘缓存约定;
// 4. 顺带提供离线账号"快速新建 / 校验昵称"的薄封装,让 UI 不用直接摸 AuthService。

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FufuLauncher.Services;

/// <summary>皮肤应用结果</summary>
public sealed class SkinApplyResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    /// <summary>识别出的皮肤规格(64×64 / 64×32)</summary>
    public string Spec { get; set; } = "";
    /// <summary>是否做过老皮肤补全</summary>
    public bool Upgraded { get; set; }
    /// <summary>最终写入的文件路径</summary>
    public string WrittenFile { get; set; } = "";
}

public sealed class OfflineSkinService
{
    /// <summary>皮肤文件体积上限(2MB 足够,防用户误挑大图)</summary>
    public const long MaxSkinFileBytes = 2 * 1024 * 1024;

    private readonly AuthService _auth;
    private readonly AccountService _accounts;

    public OfflineSkinService(AuthService auth, AccountService accounts)
    {
        _auth = auth;
        _accounts = accounts;
    }

    /// <summary>离线账号数量(账号列表里 Type == Offline 的条数)</summary>
    public int OfflineAccountCount => _accounts.Accounts.Count(a => a.Type == AccountType.Offline);

    /// <summary>
    /// 把本地皮肤文件应用到指定账号。
    /// 校验 → 规整 64×64 → 重编码 PNG → 写磁盘缓存 → 清内存缓存。
    /// </summary>
    public SkinApplyResult ApplySkinFile(GameAccount account, string skinFilePath)
    {
        var res = new SkinApplyResult();
        if (account == null) { res.Message = "请先选一个账号。"; return res; }
        if (string.IsNullOrWhiteSpace(account.Uuid)) { res.Message = "这个账号没有 UUID,没法保存皮肤。"; return res; }
        if (string.IsNullOrWhiteSpace(skinFilePath) || !File.Exists(skinFilePath))
        {
            res.Message = "皮肤文件不存在,请重新挑一个。";
            return res;
        }

        try
        {
            long len = new FileInfo(skinFilePath).Length;
            if (len == 0) { res.Message = "皮肤文件是空的(0 字节)。"; return res; }
            if (len > MaxSkinFileBytes)
            {
                res.Message = $"皮肤文件太大({StorageGuardService.FmtSize(len)}),Minecraft 皮肤贴图一般不会超过 2MB,请确认挑的是皮肤图而不是普通图片。";
                return res;
            }

            byte[] raw = File.ReadAllBytes(skinFilePath);
            var src = DecodeBitmap(raw, out string decodeError);
            if (src == null) { res.Message = decodeError; return res; }

            int w = src.PixelWidth, h = src.PixelHeight;
            if (w != 64 || (h != 64 && h != 32))
            {
                res.Message = $"图片尺寸是 {w}×{h},不是合法的 Minecraft 皮肤贴图。" +
                              "皮肤必须是 64×64(现代规格)或 64×32(老规格),请用皮肤编辑器导出后再上传。";
                return res;
            }

            byte[] png = h == 32 ? UpgradeLegacySkin(src) : EncodePng(src);
            res.Spec = $"{w}×{h}";
            res.Upgraded = h == 32;

            SkinService.WriteLocalSkin(account.Uuid, png);
            res.Ok = true;
            res.WrittenFile = SkinService.LocalSkinFile(account.Uuid);
            res.Message = $"皮肤已应用到「{account.Username}」" +
                          (res.Upgraded ? "(检测到 64×32 老皮肤,已自动补全下半身到 64×64)。" : "。");
            App.WriteAppLog($"[离线皮肤] ✓ {account.Username}({account.Type}) ← {Path.GetFileName(skinFilePath)} {res.Spec}");
            return res;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[离线皮肤] ✗ 应用失败:{ex}");
            res.Message = "皮肤应用失败:" + ex.Message;
            return res;
        }
    }

    /// <summary>移除账号的本地皮肤(回退到随机生成头像)</summary>
    public (bool Ok, string Message) RemoveSkin(GameAccount account)
    {
        if (account == null || string.IsNullOrEmpty(account.Uuid)) return (false, "请先选一个账号。");
        bool removed = SkinService.RemoveLocalSkin(account.Uuid);
        return removed
            ? (true, $"已移除「{account.Username}」的本地皮肤,头像改回自动生成的样式。")
            : (false, "这个账号本来就没有上传过皮肤文件。");
    }

    /// <summary>该账号是否有用户上传的本地皮肤</summary>
    public bool HasLocalSkin(GameAccount account) =>
        account != null && SkinService.HasLocalSkin(account.Uuid);

    /// <summary>打开皮肤缓存文件夹(方便用户自己管理)</summary>
    public string SkinDir => SkinService.LocalSkinDir;

    // ==================== 离线账号薄封装 ====================

    /// <summary>快速新建离线账号:昵称校验 → 建号 → 落盘 → 切成当前账号</summary>
    public (GameAccount? Account, string Message) CreateOfflineAccount(string nickname, bool switchToIt = true)
    {
        string? err = AuthService.ValidateOfflineNickname(nickname);
        if (err != null) return (null, err);

        string name = nickname.Trim();
        if (_accounts.Accounts.Any(a => a.Type == AccountType.Offline &&
                                        string.Equals(a.Username, name, StringComparison.OrdinalIgnoreCase)))
            return (null, $"已经有一个叫「{name}」的离线账号了,换个名字或者直接切换过去用。");

        try
        {
            var acc = _auth.LoginOffline(name);
            if (switchToIt) _accounts.SetCurrentAccount(acc.Uuid);
            else _accounts.NotifyAccountsChanged();   // 不切账号也得让列表刷新
            App.WriteAppLog($"[离线账号] ✓ 新建离线账号「{name}」({acc.Uuid})");
            return (acc, $"离线账号「{name}」已创建{(switchToIt ? "并设为当前使用的账号" : "")}。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[离线账号] ✗ 创建失败:{ex}");
            return (null, "创建离线账号失败:" + ex.Message);
        }
    }

    /// <summary>改名离线账号(改名会换 UUID,本地皮肤需一并迁移)</summary>
    public (bool Ok, string Message) RenameOfflineAccount(GameAccount account, string newNickname)
    {
        if (account == null) return (false, "请先选一个账号。");
        if (account.Type != AccountType.Offline) return (false, "只有离线账号能改名,微软账号的名字跟微软账户绑定。");
        string? err = AuthService.ValidateOfflineNickname(newNickname);
        if (err != null) return (false, err);

        string oldUuid = account.Uuid;
        string oldName = account.Username;
        string newName = newNickname.Trim();
        try
        {
            // 走 AccountService 统一入口:内部会改 UUID、写回配置、广播账号变更
            _accounts.RenameAccount(oldUuid, newName);
            var renamed = _accounts.Accounts.FirstOrDefault(a =>
                a.Type == AccountType.Offline && string.Equals(a.Username, newName, StringComparison.OrdinalIgnoreCase));
            // UUID 变了 → 老皮肤文件名对不上,顺手迁过去
            if (renamed != null) MigrateSkinFile(oldUuid, renamed.Uuid);
            App.WriteAppLog($"[离线账号] ✓ 改名「{oldName}」→「{newName}」");
            return (true, $"已改名为「{newName}」。");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[离线账号] ✗ 改名失败:{ex}");
            return (false, "改名失败:" + ex.Message);
        }
    }

    /// <summary>把旧 UUID 的皮肤文件搬到新 UUID 名下</summary>
    private static void MigrateSkinFile(string oldUuid, string newUuid)
    {
        if (string.IsNullOrEmpty(oldUuid) || string.IsNullOrEmpty(newUuid) || oldUuid == newUuid) return;
        try
        {
            string src = SkinService.LocalSkinFile(oldUuid);
            if (!File.Exists(src)) return;
            Directory.CreateDirectory(SkinService.LocalSkinDir);
            File.Move(src, SkinService.LocalSkinFile(newUuid), overwrite: true);
            SkinService.ClearCache(oldUuid);
            SkinService.ClearCache(newUuid);
            App.WriteAppLog($"[离线皮肤] 皮肤文件已跟随改名迁移 {oldUuid} → {newUuid}");
        }
        catch (Exception ex) { App.WriteAppLog($"[离线皮肤] 改名后迁移皮肤失败:{ex.Message}"); }
    }

    /// <summary>一键切换当前账号</summary>
    public (bool Ok, string Message) SwitchTo(GameAccount account)
    {
        if (account == null) return (false, "请先选一个账号。");
        _accounts.SetCurrentAccount(account.Uuid);
        return (true, $"已切换到「{account.Username}」。");
    }

    // ==================== 图像处理 ====================

    /// <summary>解码任意 WPF 支持的位图格式为 Bgra32 像素源(失败返回 null + 中文原因)</summary>
    private static BitmapSource? DecodeBitmap(byte[] raw, out string error)
    {
        error = "";
        try
        {
            BitmapDecoder decoder;
            using (var ms = new MemoryStream(raw, writable: false))
                decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            // 统一转 Bgra32,后续按像素搬运才不用管源格式
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        catch (Exception ex)
        {
            error = "这张图没法解码,请确认它是 PNG / BMP / JPG 格式的普通图片。(" + ex.Message + ")";
            return null;
        }
    }

    /// <summary>把 64×32 老皮肤补成 64×64:下半身的手臂/腿用上半身镜像填充</summary>
    private static byte[] UpgradeLegacySkin(BitmapSource src)
    {
        int w = 64, h = 64;
        var px = ReadPixels(src, 64, 32);
        var outPx = new byte[w * h * 4];
        Buffer.BlockCopy(px, 0, outPx, 0, 64 * 32 * 4);

        // 老皮肤布局:腿(0,16)-(15,31)、手臂(40,16)-(55,31);
        // 新皮肤多出的下半部分是"第二层外套":腿(0,48)-(15,63)、手臂(40,48)-(55,63)。
        // 主流做法是留空(透明)或复制第一层;这里复制第一层,保证人物不会"半透明缺胳膊少腿"。
        CopyRows(px, 64, srcY: 16, srcH: 16, outPx, dstY: 48, srcX: 0, w16: 16);
        CopyRows(px, 64, srcY: 16, srcH: 16, outPx, dstY: 48, srcX: 40, w16: 16);

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, outPx, w * 4);
        bmp.Freeze();
        return EncodePng(bmp);
    }

    private static void CopyRows(byte[] src, int srcStridePx, int srcY, int srcH,
                                 byte[] dst, int dstY, int srcX, int w16)
    {
        for (int y = 0; y < srcH; y++)
        {
            int sOff = ((srcY + y) * srcStridePx + srcX) * 4;
            int dOff = ((dstY + y) * 64 + srcX) * 4;
            Buffer.BlockCopy(src, sOff, dst, dOff, w16 * 4);
        }
    }

    /// <summary>把位图读成 Bgra32 字节数组(行宽固定 64 像素,不足部分留空)</summary>
    private static byte[] ReadPixels(BitmapSource src, int expectW, int expectH)
    {
        int w = Math.Min(expectW, src.PixelWidth);
        int h = Math.Min(expectH, src.PixelHeight);
        var buf = new byte[expectW * expectH * 4];
        int stride = expectW * 4;
        var tmp = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            src.CopyPixels(new System.Windows.Int32Rect(0, y, w, 1), tmp, w * 4, 0);
            Buffer.BlockCopy(tmp, 0, buf, y * stride, w * 4);
        }
        return buf;
    }

    private static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
