// Copyright © FufuLauncher
//
// 拖拽 zip 自动识别路由(LauncherX-3):
// 把压缩包 / 文件夹拖到启动器窗口任意位置,先由 ZipDropService 只读中央目录做类型判定
// (模组包 / 资源包 / 光影包 / 整合包 / 存档 / 不认识),再按类型分流:
//   · 整合包 → 引导新建版本并走完导入流程;
//   · 模组 / 资源包 / 光影包 → 选一个版本装进去;
//   · 存档 → 提示并给存档目录入口(存档受硬性保护,不做自动覆盖);
//   · 认不出来 → 把判定依据原样告诉用户,不瞎猜。
// 返回 false 表示这批文件与本功能无关,调用方可继续走自己原有的拖拽逻辑(如自定义背景图)。

using System.IO;
using System.Windows;
using FufuLauncher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FufuLauncher.Next.UI;

internal static class DropRouter
{
    /// <summary>处理一次拖拽投递;有任一文件被识别为可处理类型时返回 true</summary>
    public static bool Handle(Window? owner, string[]? files)
    {
        if (files == null || files.Length == 0) return false;
        // 只挑启动器认的扩展名 / 文件夹,图片之类的直接放行给原有逻辑
        var cand = files.Where(f => ZipDropService.IsSupportedFile(f)).ToArray();
        if (cand.Length == 0) return false;

        var zip = App.Services.GetRequiredService<ZipDropService>();
        var instances = App.Services.GetRequiredService<InstanceService>();
        List<DropAnalysis> list;
        try { list = zip.AnalyzeAll(cand); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[拖拽] 识别异常:{ex}");
            DialogKit.Error("识别拖入的文件失败:" + ex.Message, "拖拽识别", owner);
            return true;
        }

        var handled = list.Where(a => a.Kind != DropKind.Unknown && a.Kind != DropKind.Unsupported).ToList();
        var refused = list.Where(a => a.Kind == DropKind.Unknown || a.Kind == DropKind.Unsupported).ToList();
        if (handled.Count == 0)
        {
            string why = string.Join("\n", refused.Select(a => $"{a.FileName}:{a.Reason}"));
            DialogKit.Info("这些文件启动器认不出来,没法自动处理:\n\n" + Trim(why, 800), "拖拽识别", owner);
            return true;
        }

        App.WriteAppLog($"[拖拽] 识别 {handled.Count} 个可处理文件:{string.Join(",", handled.Select(a => a.KindDisplay))}");

        // ---- 整合包优先:走新建版本导入流程 ----
        foreach (var pack in handled.Where(a => a.Kind == DropKind.ModPack))
            ImportModPack(owner, zip, pack);

        // ---- 模组 ----
        var mods = handled.Where(a => a.Kind is DropKind.Mod or DropKind.MultiMod).ToList();
        if (mods.Count > 0) InstallMods(owner, zip, instances, mods);

        // ---- 资源包 / 光影包 ----
        var packs = handled.Where(a => a.Kind is DropKind.ResourcePack or DropKind.ShaderPack).ToList();
        if (packs.Count > 0) InstallContentPacks(owner, zip, instances, packs);

        // ---- 存档 ----
        foreach (var save in handled.Where(a => a.Kind == DropKind.SaveGame))
        {
            var inst = PickInstance(owner, instances, $"把存档「{save.SuggestedName}」放进哪个版本?");
            if (inst == null) continue;
            string dir = instances.GetSavesDir(inst.Id);
            if (PanelKit.OpenFolder(dir))
                DialogKit.Info($"已打开「{inst.Name}」的存档目录。\n存档受硬性保护,启动器不会自动覆盖,请自己把 {save.FileName} 解压进去。",
                    "存档导入", owner);
        }

        // ---- 认不出来的顺带告知 ----
        if (refused.Count > 0)
        {
            string why = string.Join("\n", refused.Select(a => $"{a.FileName}:{a.Reason}"));
            DialogKit.Info("下面这些文件没被处理:\n\n" + Trim(why, 600), "拖拽识别", owner);
        }
        return true;
    }

    // ==================== 整合包 ====================

    private static void ImportModPack(Window? owner, ZipDropService zip, DropAnalysis a)
    {
        bool isDir = Directory.Exists(a.Path);
        if (!DialogKit.Confirm(
                $"识别为整合包({a.Reason})。\n\n文件:{a.FileName}\n大小:{a.SizeDisplay}\n建议版本名:{a.SuggestedName}\n\n要新建一个版本并导入吗?",
                "发现整合包", "新建并导入", danger: false, owner: owner)) return;

        string? name = DialogKit.Input("给这个版本起个名字:", "导入整合包", a.SuggestedName, owner);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            ModPackImportResult result = isDir
                ? DialogKit.RunWithProgress("正在导入整合包文件夹…",
                    report => zip.ImportModPackDirAsync(a.Path, name.Trim(), report), owner)
                : DialogKit.RunWithProgress("正在导入整合包…",
                    report => zip.ImportModPackAsync(a.Path, name.Trim(), report), owner);

            if (result.Ok)
            {
                App.WriteAppLog($"[拖拽] 整合包导入成功:{a.FileName} → {result.Instance?.Name}");
                DialogKit.Success(result.Message, "导入完成", owner);
            }
            else
            {
                App.WriteAppLog($"[拖拽] 整合包导入失败:{result.Message}");
                DialogKit.Error("导入失败:" + result.Message, "导入整合包", owner);
            }
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[拖拽] 整合包导入异常:{ex}");
            DialogKit.Error("导入异常:" + ex.Message, "导入整合包", owner);
        }
    }

    // ==================== 模组 ====================

    private static void InstallMods(Window? owner, ZipDropService zip, InstanceService instances, List<DropAnalysis> mods)
    {
        var inst = PickInstance(owner, instances, $"把这 {mods.Count} 个模组装进哪个版本?");
        if (inst == null) return;

        var files = mods.SelectMany(a => a.Kind == DropKind.MultiMod && a.ModFiles.Count > 0
            ? (IEnumerable<string>)a.ModFiles
            : new[] { a.Path }).ToList();
        DropInstallResult res;
        try { res = zip.InstallMods(files, inst.Id); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[拖拽] 模组安装异常:{ex}");
            DialogKit.Error("模组安装异常:" + ex.Message, "拖拽安装模组", owner);
            return;
        }

        string note = mods.Any(a => a.Kind == DropKind.MultiMod && a.Reason.Contains("解压"))
            ? "\n\n提示:散装 jar 压缩包只装了包内的 jar 文件,其余内容需要你自己解压。" : "";
        if (res.Ok) DialogKit.Success($"{res.Message}{note}", "拖拽安装模组", owner);
        else DialogKit.Error($"{res.Message}{note}", "拖拽安装模组", owner);
    }

    // ==================== 资源包 / 光影包 ====================

    private static void InstallContentPacks(Window? owner, ZipDropService zip, InstanceService instances, List<DropAnalysis> packs)
    {
        var inst = PickInstance(owner, instances, $"把这 {packs.Count} 个资源/光影包装进哪个版本?");
        if (inst == null) return;

        var ok = new List<string>();
        var fail = new List<string>();
        foreach (var group in packs.GroupBy(p => p.Kind))
        {
            DropInstallResult res = group.Key == DropKind.ShaderPack
                ? zip.InstallShaderPacks(group.Select(g => g.Path), inst.Id)
                : zip.InstallResourcePacks(group.Select(g => g.Path), inst.Id);
            ok.AddRange(res.Installed);
            fail.AddRange(res.Failed);
            if (!res.Ok) App.WriteAppLog($"[拖拽] {group.Key} 安装:{res.Message}");
        }

        string what = packs.Select(p => p.KindDisplay).Distinct().Aggregate("", (s, x) => string.IsNullOrEmpty(s) ? x : s + " / " + x);
        if (fail.Count == 0) DialogKit.Success($"{what}已装进「{inst.Name}」:{ok.Count} 个", "拖拽安装", owner);
        else DialogKit.Error($"{what}安装:成功 {ok.Count} 个,失败 {fail.Count} 个\n{Trim(string.Join("\n", fail.Take(6)), 500)}",
            "拖拽安装", owner);
    }

    // ==================== 版本选择 ====================

    /// <summary>从本地版本列表里挑一个;只有一个时直接用它,一个都没有时给出提示</summary>
    private static GameInstance? PickInstance(Window? owner, InstanceService instances, string prompt)
    {
        instances.RefreshInstances();
        if (instances.Instances.Count == 0)
        {
            DialogKit.Info("本地还没有游戏版本,请先去「版本管理 → 新装版本」装一个", "选择版本", owner);
            return null;
        }
        if (instances.Instances.Count == 1) return instances.Instances[0];

        var names = instances.Instances.Select(i => $"{i.Name}(MC {i.VersionId})").ToList();
        string? picked = DialogKit.PickFromList(prompt, names, "选择版本", owner);
        if (string.IsNullOrEmpty(picked)) return null;
        int idx = names.IndexOf(picked);
        return idx >= 0 ? instances.Instances[idx] : null;
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "\n…";
}
