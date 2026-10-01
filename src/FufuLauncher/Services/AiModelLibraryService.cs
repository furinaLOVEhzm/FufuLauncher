// AiModelLibraryService.cs — 泡芙助理本地模型库(模型由用户自行准备维护)
// Copyright © FufuLauncher
//
// 职责:管理「用户自己拖入 / 手动准备」的 GGUF 模型 —— 导入(单个文件或整个文件夹递归扫描)、
//   登记(名称 / 大小 / 导入时间)、切换活跃模型、删除、持久化注册表;构造时扫描 models 目录
//   与注册表对账(自动登记遗漏项、剔除失效项)。
//
// 设计约束(本轮需求定稿):
//   - 程序不内置模型、不联网下载模型;下载链接仅在 UI 呈现,用户自行获取后拖入即可;
//   - 模型一律存放于 AppPaths.Root\models(数据根内、随程序目录,绝不写 C 盘用户目录);
//   - 向后兼容:若 models 目录已存在旧发布包内置的 Qwen3-4B-Q4_K_M.gguf,首启自动登记并优先设为活跃。
//
// 线程安全:注册表读写用 _gate 锁保护;导入 / 删除等耗时文件操作由调用方放到后台线程
//   (ImportPathsAsync 内部已 Task.Run);Changed 事件在调用线程触发,UI 侧自行 Dispatcher 封送。

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;   // [JsonIgnore] 特性所在命名空间(与 JsonSerializer 分开)

namespace FufuLauncher.Services;

/// <summary>单个已登记模型的元信息(注册表持久化实体)</summary>
public sealed class AiModelInfo
{
    /// <summary>稳定标识:取模型文件名(models 目录内文件名唯一)</summary>
    public string Id { get; set; } = "";
    /// <summary>文件名(含 .gguf 扩展名)</summary>
    public string FileName { get; set; } = "";
    /// <summary>展示名(默认去扩展名)</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>文件字节大小</summary>
    public long SizeBytes { get; set; }
    /// <summary>导入时间(手动拷入的既有文件取其最后修改时间)</summary>
    public DateTime ImportedAt { get; set; }

    [JsonIgnore] public string FullPath => Path.Combine(AiModelLibraryService.ModelsDir, FileName);
    [JsonIgnore] public bool FileExists => File.Exists(FullPath);
}

/// <summary>一次导入操作的结果汇总(供 UI 反馈)</summary>
public sealed class AiModelImportOutcome
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> Notes { get; set; } = new();
    public AiModelInfo? First { get; set; }

    public string Summary
    {
        get
        {
            var s = $"成功导入 {Imported} 个模型";
            if (Skipped > 0) s += $",跳过 {Skipped} 个";
            if (Notes.Count > 0) s += "。" + string.Join(";", Notes.Take(4));
            return s;
        }
    }
}

public sealed class AiModelLibraryService
{
    /// <summary>模型存放目录:数据根下的 models(不属于 12 个规范子目录,按需创建)</summary>
    public static string ModelsDir => Path.Combine(AppPaths.Root, "models");

    private static string RegistryFile => Path.Combine(ModelsDir, "models.registry.json");

    /// <summary>推荐模型文件名(与旧发布包内置的一致,用于活跃优先级与向后兼容)</summary>
    public const string RecommendedModelName = "Qwen3-4B-Q4_K_M.gguf";

    /// <summary>模型下载来源(UI 呈现给用户,程序本身不联网下载)</summary>
    public static readonly (string Label, string Url)[] DownloadSources =
    {
        ("ModelScope 魔搭(国内直连)", "https://modelscope.cn/models/Qwen/Qwen3-4B-GGUF"),
        ("HuggingFace", "https://huggingface.co/Qwen/Qwen3-4B-GGUF"),
    };

    private readonly object _gate = new();
    private List<AiModelInfo> _models = new();
    private string _activeId = "";

    /// <summary>模型集合或活跃项发生变化(导入 / 删除 / 切换 / 对账)时触发</summary>
    public event Action? Changed;

    public AiModelLibraryService()
    {
        try { Directory.CreateDirectory(ModelsDir); } catch { /* 首次导入时再建 */ }
        LoadRegistry();
        ReconcileWithDisk();
    }

    // ==================== 查询 ====================

    /// <summary>当前已登记模型快照(按导入时间排序)</summary>
    public IReadOnlyList<AiModelInfo> Models { get { lock (_gate) return _models.ToList(); } }

    /// <summary>活跃模型:优先取显式设定的活跃项,失效则回退第一个存在的模型</summary>
    public AiModelInfo? Active
    {
        get
        {
            lock (_gate)
                return _models.FirstOrDefault(m => m.Id == _activeId && m.FileExists)
                    ?? _models.FirstOrDefault(m => m.FileExists);
        }
    }

    /// <summary>活跃模型完整路径(无可用模型时为 null)</summary>
    public string? ActiveModelPath => Active?.FullPath;

    /// <summary>是否存在至少一个可用模型</summary>
    public bool HasAnyModel { get { lock (_gate) return _models.Any(m => m.FileExists); } }

    /// <summary>供 AiEngineService 解析实际加载路径:活跃模型存在则用之,
    /// 否则回退旧发布包内置的固定模型(向后兼容,老用户无感)</summary>
    public string? ResolveLoadPath()
    {
        var p = ActiveModelPath;
        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        string legacy = Path.Combine(ModelsDir, AiEngineService.ModelFileName);
        return File.Exists(legacy) ? legacy : null;
    }

    // ==================== 导入 ====================

    /// <summary>后台导入一批路径(文件按 .gguf 收录;文件夹递归扫描其中全部 .gguf)</summary>
    public Task<AiModelImportOutcome> ImportPathsAsync(IEnumerable<string> paths, IProgress<string>? progress = null)
        => Task.Run(() => ImportPaths(paths, progress));

    private AiModelImportOutcome ImportPaths(IEnumerable<string> paths, IProgress<string>? progress)
    {
        var outcome = new AiModelImportOutcome();
        var ggufs = new List<string>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                if (Directory.Exists(raw))
                {
                    progress?.Report($"正在扫描文件夹:{Path.GetFileName(raw.TrimEnd('\\', '/'))} …");
                    ggufs.AddRange(SafeEnumerateGguf(raw));
                }
                else if (File.Exists(raw) && IsGgufExt(raw))
                {
                    ggufs.Add(raw);
                }
                else if (File.Exists(raw))
                {
                    outcome.Skipped++;
                    outcome.Notes.Add($"{Path.GetFileName(raw)} 不是 .gguf 模型,已跳过");
                }
            }
            catch (Exception ex) { outcome.Notes.Add($"{raw}:{ex.Message}"); }
        }

        foreach (var src in ggufs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = ImportOne(src, outcome, progress);
                if (info != null) { outcome.Imported++; outcome.First ??= info; }
            }
            catch (Exception ex)
            {
                outcome.Skipped++;
                outcome.Notes.Add($"{Path.GetFileName(src)} 导入失败:{ex.Message}");
                App.WriteAppLog($"[模型库] 导入失败:{src} -> {ex.Message}");
            }
        }

        if (outcome.Imported > 0)
        {
            SaveRegistry();
            Changed?.Invoke();
        }
        App.WriteAppLog($"[模型库] 导入结束:{outcome.Summary}");
        return outcome;
    }

    /// <summary>导入单个 .gguf:校验魔数 → 已在 models 目录则就地登记,否则复制进来 → 写注册表</summary>
    private AiModelInfo? ImportOne(string src, AiModelImportOutcome outcome, IProgress<string>? progress)
    {
        string name = Path.GetFileName(src);
        progress?.Report($"校验 {name} …");

        if (!IsGgufFile(src))
        {
            outcome.Skipped++;
            outcome.Notes.Add($"{name} 不是有效的 GGUF 模型(魔数校验未通过)");
            return null;
        }

        string dest = Path.Combine(ModelsDir, name);
        bool alreadyInPlace = IsSamePath(src, dest);
        if (!alreadyInPlace)
        {
            // 目标重名:加序号避免覆盖既有模型
            dest = UniqueDest(name);
            string destName = Path.GetFileName(dest);
            progress?.Report($"复制 {name} → models\\{destName} …");
            Directory.CreateDirectory(ModelsDir);
            File.Copy(src, dest, false);
        }

        var fi = new FileInfo(dest);
        string finalName = Path.GetFileName(dest);
        lock (_gate)
        {
            var existing = _models.FirstOrDefault(m => string.Equals(m.FileName, finalName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.SizeBytes = fi.Length;      // 已登记:仅刷新大小
                outcome.Notes.Add($"{finalName} 已在模型库中");
                return existing;
            }
            var info = new AiModelInfo
            {
                Id = finalName,
                FileName = finalName,
                DisplayName = Path.GetFileNameWithoutExtension(finalName),
                SizeBytes = fi.Length,
                ImportedAt = DateTime.Now,
            };
            _models.Add(info);
            _models = _models.OrderBy(m => m.ImportedAt).ThenBy(m => m.FileName).ToList();
            // 首个导入的模型自动设为活跃
            if (string.IsNullOrEmpty(_activeId) || !_models.Any(m => m.Id == _activeId))
                _activeId = info.Id;
            return info;
        }
    }

    // ==================== 切换 / 删除 ====================

    /// <summary>设定活跃模型(供下次加载 / 切换使用)。成功返回 true</summary>
    public bool SetActive(string id)
    {
        lock (_gate)
        {
            if (!_models.Any(m => m.Id == id && m.FileExists)) return false;
            if (_activeId == id) { return true; }
            _activeId = id;
        }
        SaveRegistry();
        Changed?.Invoke();
        App.WriteAppLog($"[模型库] 切换活跃模型:{id}");
        return true;
    }

    /// <summary>删除模型:移除 models 目录内的文件并注销登记。
    /// 注意:若该模型正被引擎加载(文件句柄占用),文件删除会失败 —— 调用方应先卸载引擎再删</summary>
    public bool Delete(string id)
    {
        AiModelInfo? target;
        lock (_gate) { target = _models.FirstOrDefault(m => m.Id == id); }
        if (target == null) return false;

        bool fileGone = true;
        try
        {
            if (File.Exists(target.FullPath)) File.Delete(target.FullPath);
        }
        catch (Exception ex)
        {
            fileGone = false;
            App.WriteAppLog($"[模型库] 删除模型文件失败(可能正被占用):{ex.Message}");
        }

        lock (_gate)
        {
            _models.RemoveAll(m => m.Id == id);
            if (_activeId == id)
                _activeId = _models.FirstOrDefault(m => string.Equals(m.Id, RecommendedModelName, StringComparison.OrdinalIgnoreCase))?.Id
                         ?? _models.FirstOrDefault(m => m.FileExists)?.Id ?? "";
        }
        SaveRegistry();
        Changed?.Invoke();
        App.WriteAppLog($"[模型库] 删除模型:{id}(文件已删={fileGone})");
        return fileGone;
    }

    // ==================== 注册表持久化 ====================

    private sealed class Registry
    {
        public string ActiveId { get; set; } = "";
        public List<AiModelInfo> Models { get; set; } = new();
    }

    private void LoadRegistry()
    {
        try
        {
            if (!File.Exists(RegistryFile)) return;
            var reg = JsonSerializer.Deserialize<Registry>(File.ReadAllText(RegistryFile));
            if (reg?.Models == null) return;
            lock (_gate) { _models = reg.Models; _activeId = reg.ActiveId ?? ""; }
        }
        catch (Exception ex) { App.WriteAppLog($"[模型库] 注册表读取失败(将按磁盘重建):{ex.Message}"); }
    }

    private void SaveRegistry()
    {
        try
        {
            Directory.CreateDirectory(ModelsDir);
            Registry reg;
            lock (_gate) { reg = new Registry { ActiveId = _activeId, Models = _models.ToList() }; }
            File.WriteAllText(RegistryFile, JsonSerializer.Serialize(reg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { App.WriteAppLog($"[模型库] 注册表保存失败:{ex.Message}"); }
    }

    /// <summary>与磁盘对账:剔除已不存在的登记项,登记目录里遗漏的 .gguf(含旧内置模型 / 用户手动拷入)</summary>
    private void ReconcileWithDisk()
    {
        List<string> diskNames;
        try { diskNames = SafeEnumerateGguf(ModelsDir).Select(p => Path.GetFileName(p)!).ToList(); }
        catch (Exception ex) { App.WriteAppLog($"[模型库] 扫描 models 目录失败:{ex.Message}"); return; }

        bool dirty = false;
        lock (_gate)
        {
            int before = _models.Count;
            _models.RemoveAll(m => !diskNames.Contains(m.FileName, StringComparer.OrdinalIgnoreCase));
            if (_models.Count != before) dirty = true;

            foreach (var fn in diskNames)
            {
                if (_models.Any(m => string.Equals(m.FileName, fn, StringComparison.OrdinalIgnoreCase))) continue;
                long len = 0; DateTime mt = DateTime.Now;
                try { var fi = new FileInfo(Path.Combine(ModelsDir, fn)); len = fi.Length; mt = fi.LastWriteTime; } catch { }
                _models.Add(new AiModelInfo
                {
                    Id = fn,
                    FileName = fn,
                    DisplayName = Path.GetFileNameWithoutExtension(fn),
                    SizeBytes = len,
                    ImportedAt = mt,
                });
                dirty = true;
            }

            _models = _models.OrderBy(m => m.ImportedAt).ThenBy(m => m.FileName).ToList();

            if (string.IsNullOrEmpty(_activeId) || !_models.Any(m => m.Id == _activeId))
                _activeId = _models.FirstOrDefault(m => string.Equals(m.Id, RecommendedModelName, StringComparison.OrdinalIgnoreCase))?.Id
                         ?? _models.FirstOrDefault()?.Id ?? "";
        }
        if (dirty) SaveRegistry();
    }

    // ==================== 工具 ====================

    private static IEnumerable<string> SafeEnumerateGguf(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories))
            yield return f;
    }

    private static bool IsGgufExt(string path) => path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>校验 GGUF 魔数(前 4 字节为 ASCII "GGUF"),与 AiEngineService 加载前自检同源</summary>
    private static bool IsGgufFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[4];
            int read = 0;
            while (read < 4) { int n = fs.Read(magic.Slice(read)); if (n <= 0) break; read += n; }
            return read == 4 && magic[0] == (byte)'G' && magic[1] == (byte)'G' && magic[2] == (byte)'U' && magic[3] == (byte)'F';
        }
        catch { return false; }
    }

    private static bool IsSamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>目标重名时生成唯一文件名:foo.gguf → foo (2).gguf → foo (3).gguf …</summary>
    private string UniqueDest(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string candidate = Path.Combine(ModelsDir, fileName);
        int i = 2;
        while (File.Exists(candidate) || ModelNameTaken(candidate))
        {
            candidate = Path.Combine(ModelsDir, $"{baseName} ({i}){ext}");
            i++;
        }
        return candidate;
    }

    private bool ModelNameTaken(string fullPath)
    {
        string fn = Path.GetFileName(fullPath);
        lock (_gate) return _models.Any(m => string.Equals(m.FileName, fn, StringComparison.OrdinalIgnoreCase));
    }
}
