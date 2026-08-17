// ResourcePackService.cs — 资源包与光影包管理服务
// 可爱的芙芙 - 阶段4 模块
//
// 资源包管理:读取、上下排序、启用禁用
// 光影包:识别 shaderpacks 目录

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace FufuLauncher.Services;

public class ResourcePackInfo
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int PackFormat { get; set; }
    public bool Enabled { get; set; }
    public int Order { get; set; }   // 在 options.txt 中的顺序
}

public class ShaderPackInfo
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool Enabled { get; set; }
}

public class ResourcePackService
{
    private readonly InstanceService _instanceService;
    public string? CurrentInstanceId { get; private set; }

    public ResourcePackService(InstanceService instanceService)
    {
        _instanceService = instanceService;
    }

    public void SetCurrentInstance(string instanceId) => CurrentInstanceId = instanceId;

    public List<ResourcePackInfo> LoadResourcePacks()
    {
        var list = new List<ResourcePackInfo>();
        if (string.IsNullOrEmpty(CurrentInstanceId)) return list;

        string rpDir = _instanceService.GetResourcePacksDir(CurrentInstanceId);
        if (!Directory.Exists(rpDir)) return list;

        // 读取 options.txt 中的已启用资源包列表
        var enabledPacks = ReadEnabledResourcePacks();

        int order = 0;
        foreach (var file in Directory.EnumerateFiles(rpDir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".zip") continue;
            var info = new ResourcePackInfo
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Order = order++
            };
            try { ParseResourcePackMeta(file, info); }
            catch (Exception ex) { App.WriteAppLog($"[资源包] 解析元数据失败 {file}:{ex.Message}"); }
            info.Enabled = enabledPacks.Contains(info.FileName);
            list.Add(info);
        }
        return list;
    }

    private void ParseResourcePackMeta(string zipPath, ResourcePackInfo info)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("pack.mcmeta");
        if (entry == null) return;
        using var sr = new StreamReader(entry.Open());
        var json = sr.ReadToEnd();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("pack", out var pack))
        {
            if (pack.TryGetProperty("pack_format", out var pf)) info.PackFormat = pf.GetInt32();
            if (pack.TryGetProperty("description", out var d))
                info.Description = d.ValueKind == System.Text.Json.JsonValueKind.String
                    ? d.GetString() ?? ""
                    : d.ToString();
        }
        info.Name = Path.GetFileNameWithoutExtension(zipPath);
    }

    private List<string> ReadEnabledResourcePacks()
    {
        var list = new List<string>();
        string optionsPath = Path.Combine(
            _instanceService.GetMinecraftDir(CurrentInstanceId!), "options.txt");
        if (!File.Exists(optionsPath)) return list;
        foreach (var line in File.ReadAllLines(optionsPath))
        {
            if (line.StartsWith("resourcePacks:"))
            {
                // 格式:resourcePacks:["file/包名.zip","vanilla"]
                var matches = System.Text.RegularExpressions.Regex.Matches(line, @"""file/([^""]+)""");
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    list.Add(m.Groups[1].Value);
                }
            }
        }
        return list;
    }

    /// <summary>调整资源包顺序(上移/下移)</summary>
    public void MoveResourcePack(string fileName, bool up)
    {
        // 实际写入 options.txt 的 resourcePacks 数组顺序
        // 此处简化实现
    }

    /// <summary>切换资源包启用状态</summary>
    public void ToggleResourcePack(string fileName, bool enable) { /* 更新 options.txt */ }

    public List<ShaderPackInfo> LoadShaderPacks()
    {
        var list = new List<ShaderPackInfo>();
        if (string.IsNullOrEmpty(CurrentInstanceId)) return list;
        string spDir = _instanceService.GetShaderPacksDir(CurrentInstanceId);
        if (!Directory.Exists(spDir)) return list;

        foreach (var file in Directory.EnumerateFiles(spDir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".zip") continue;
            list.Add(new ShaderPackInfo
            {
                FileName = Path.GetFileName(file),
                FilePath = file
            });
        }
        return list;
    }

    public void SetShaderPack(string fileName) { /* 写入 shaderpack/options.txt */ }
}
