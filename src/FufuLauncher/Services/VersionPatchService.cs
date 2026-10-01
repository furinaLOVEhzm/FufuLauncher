// Copyright © FufuLauncher
//
// 版本补丁服务 —— MMC 同款「继承补丁」模型:
//   简易模式:选择 Forge / Fabric / Quilt + 版本号,复用既有加载器安装链
//             生成带 inheritsFrom 的派生版本,游戏本体与依赖全部复用,不重复下载;
//   高级模式:用户粘贴/导入任意 JSON 补丁,与当前顶层版本做对象级深度合并,
//             只覆盖补丁指定的节点(对象递归合并,数组/标量整体替换),生成新派生版本。
// 铁律:原始版本 JSON(versions/{原版}/{原版}.json)全程只读,任何修改只写新目录
//       versions/{派生id}/{派生id}.json,经 inheritsFrom 链回溯复用游戏本体。
// 本服务同时为泡芙助理预留调用接口:高危操作统一经 highRiskConfirm 回调确认,
// AI 侧调用时传入弹窗确认委托即可,返回 false 则中止且不产生任何落盘改动。

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace FufuLauncher.Services;

/// <summary>补丁执行结果(含新生成的派生版本 id,便于界面/日志追溯)</summary>
public class PatchResult
{
    public bool Success { get; set; }
    /// <summary>中文错误提示(仅失败时有值)</summary>
    public string? Error { get; set; }
    /// <summary>生成的派生版本 JSON 的 id(成功时有值,已写入实例 LoaderVersionId)</summary>
    public string? NewVersionId { get; set; }

    public static PatchResult Ok(string newVersionId) => new() { Success = true, NewVersionId = newVersionId };
    public static PatchResult Fail(string msg) => new() { Success = false, Error = msg };
}

public class VersionPatchService
{
    private readonly InstanceService _instanceService;
    private readonly ModLoaderInstallService _loaderInstall;

    public VersionPatchService(InstanceService instanceService, ModLoaderInstallService loaderInstall)
    {
        _instanceService = instanceService;
        _loaderInstall = loaderInstall;
    }

    // ==================== 简易模式:加载器 → 继承补丁 ====================

    /// <summary>为指定版本安装加载器并生成 MMC 风格继承补丁(派生版本)。
    /// 复用现有加载器安装链:派生 JSON 以 inheritsFrom 指向原版,原始 version.json 保持只读。
    /// highRiskConfirm 为泡芙助理等外部调用方预留:执行前弹窗确认,返回 false 即中止;传 null 表示调用方已自行确认</summary>
    public async Task<PatchResult> ApplyLoaderPatchAsync(string instanceId, string loaderKind, string loaderVersion,
                                                         Func<string, bool>? highRiskConfirm = null)
    {
        var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
        if (inst == null) return PatchResult.Fail($"找不到游戏版本:{instanceId}");
        if (string.IsNullOrWhiteSpace(loaderVersion)) return PatchResult.Fail("请先选择加载器版本号");

        string prompt = $"将为版本「{inst.Name}」安装 {loaderKind} {loaderVersion},生成派生版本(原始版本文件不会被修改)。是否继续?";
        if (highRiskConfirm != null && !highRiskConfirm(prompt))
        {
            App.WriteAppLog($"[版本补丁] 用户中止加载器补丁:{inst.Name} {loaderKind} {loaderVersion}");
            return PatchResult.Fail("已取消");
        }

        try
        {
            var res = await _loaderInstall.InstallLoaderAsync(instanceId, inst.VersionId, loaderKind, loaderVersion);
            if (!res.Success) return PatchResult.Fail(res.ErrorMessage ?? "加载器安装失败");
            _instanceService.RefreshInstances();
            App.WriteAppLog($"[版本补丁] 加载器补丁完成:{inst.Name} → {res.LoaderVersionId}");
            return PatchResult.Ok(res.LoaderVersionId ?? "");
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本补丁] 加载器补丁异常:{ex.Message}");
            return PatchResult.Fail("加载器安装失败: " + ex.Message);
        }
    }

    // ==================== 高级模式:JSON 补丁深度合并 ====================

    /// <summary>执行 JSON 补丁:与当前顶层版本深度合并后生成新派生版本。
    /// 合并规则:对象递归合并、只覆盖补丁出现的节点;数组与标量由补丁整体替换。
    /// 语法错误/根非对象/继承目标缺失一律拦截并返回中文原因,绝不落盘损坏版本。
    /// highRiskConfirm 为泡芙助理等外部调用方预留(同 ApplyLoaderPatchAsync)</summary>
    public PatchResult ApplyJsonPatch(string instanceId, string patchJson, Func<string, bool>? highRiskConfirm = null)
    {
        var inst = _instanceService.Instances.Find(i => i.Id == instanceId);
        if (inst == null) return PatchResult.Fail($"找不到游戏版本:{instanceId}");
        if (string.IsNullOrWhiteSpace(patchJson)) return PatchResult.Fail("补丁内容为空");

        // ---- 1. 语法校验(失败即拦截,不生成任何文件)----
        JsonObject patch;
        try
        {
            var node = JsonNode.Parse(patchJson);
            if (node is not JsonObject obj) return PatchResult.Fail("补丁必须是 JSON 对象(以 { 开始、} 结束)");
            if (obj.Count == 0) return PatchResult.Fail("补丁是空对象,没有任何可合并的节点");
            patch = obj;
        }
        catch (JsonException ex)
        {
            return PatchResult.Fail("JSON 语法错误:" + ex.Message);
        }

        // ---- 2. 继承链:补丁未指定 inheritsFrom 时,默认挂在当前顶层版本之下 ----
        string currentTop = !string.IsNullOrEmpty(inst.LoaderVersionId) ? inst.LoaderVersionId! : inst.VersionId;
        string inherits = patch["inheritsFrom"]?.GetValue<string>() ?? currentTop;
        if (string.IsNullOrWhiteSpace(inherits)) return PatchResult.Fail("inheritsFrom 不能为空字符串");
        if (!Directory.Exists(Path.Combine(AppPaths.Versions, inherits)))
            return PatchResult.Fail($"补丁的 inheritsFrom 目标版本不存在:{inherits}");

        // ---- 3. 高危确认(泡芙助理调用时在此弹窗)----
        string prompt = $"将对版本「{inst.Name}」应用 JSON 补丁,基于 {inherits} 生成新派生版本(原始版本文件不会被修改)。是否继续?";
        if (highRiskConfirm != null && !highRiskConfirm(prompt))
        {
            App.WriteAppLog($"[版本补丁] 用户中止 JSON 补丁:{inst.Name}");
            return PatchResult.Fail("已取消");
        }

        try
        {
            // ---- 4. 深度合并:{id, inheritsFrom} 基底 + 补丁覆盖;id 最终强制为新派生 id ----
            string newId = MakeDerivedId(inherits);
            var merged = new JsonObject
            {
                ["id"] = newId,
                ["inheritsFrom"] = inherits
            };
            DeepMerge(merged, patch);
            merged["id"] = newId;   // 补丁若夹带 id 一律无效,防止覆盖派生 id 造成链断裂

            // ---- 5. 落盘新派生版本(原始版本 JSON 全程未写入)----
            string dir = Path.Combine(AppPaths.Versions, newId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{newId}.json"),
                merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            inst.LoaderVersionId = newId;
            _instanceService.SaveInstance(inst);
            _instanceService.RefreshInstances();
            App.WriteAppLog($"[版本补丁] JSON 补丁完成:{inst.Name} → {newId}(继承 {inherits},合并 {patch.Count} 个顶层节点)");
            return PatchResult.Ok(newId);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[版本补丁] JSON 补丁异常:{ex.Message}");
            return PatchResult.Fail("补丁执行失败: " + ex.Message);
        }
    }

    // ==================== 内部工具 ====================

    /// <summary>对象级深度合并:补丁节点覆盖基底同名节点;双方同为对象则递归,否则补丁整体替换</summary>
    private static void DeepMerge(JsonObject target, JsonObject patch)
    {
        foreach (var kv in patch)
        {
            if (target.TryGetPropertyValue(kv.Key, out var existing)
                && existing is JsonObject targetObj && kv.Value is JsonObject patchObj)
            {
                DeepMerge(targetObj, patchObj);
            }
            else
            {
                target[kv.Key] = kv.Value?.DeepClone();
            }
        }
    }

    /// <summary>生成不与现有版本目录冲突的派生 id:{继承源}-patch-{时分秒},重复则追加序号</summary>
    private static string MakeDerivedId(string parentId)
    {
        string baseId = $"{Sanitize(parentId)}-patch-{DateTime.Now:HHmmss}";
        string id = baseId;
        int n = 2;
        while (Directory.Exists(Path.Combine(AppPaths.Versions, id))
               || File.Exists(Path.Combine(AppPaths.Versions, id, $"{id}.json")))
        {
            id = $"{baseId}-{n++}";
        }
        return id;
    }

    /// <summary>派生 id 清洗:仅保留字母数字与 . _ - 连字符(版本目录名安全)</summary>
    private static string Sanitize(string s)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            chars[i] = char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_';
        }
        return new string(chars);
    }
}
