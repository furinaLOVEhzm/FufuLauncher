// AiEngineService.cs — 本地自包含 AI 推理引擎
//
// 模型:由用户自行准备并拖入(APP\MCGAME\models\),程序不内置、不联网下载;
//       加载哪个模型由 AiModelLibraryService 的活跃项决定(兼容旧包内置的 Qwen3-4B)。
// 推理:LLamaSharp(llama.cpp 绑定),部署了 CUDA12 后端时(RTX30〜50 系)优先,否则/老电脑自动走 CPU。
//
// 显存策略:
//   - 显存全量利用:不设人为上限,全层卸载把权重/KV/计算缓冲全部放进可用显存;
//   - 显存 < 4GB 的 GPU 一律走 CPU 推理(核显/亮机卡场景,避免与显示输出抢显存)。
//
// 线程安全:单例;EnsureLoadedAsync 并发去重;推理调用方自行保证不并发 ChatAsync。

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace FufuLauncher.Services;

public class AiEngineService
{
    public enum EngineState { Unloaded, Loading, Ready, NoModel, Error }

    /// <summary>模型文件名(固定,随发布包内置)</summary>
    public const string ModelFileName = "Qwen3-4B-Q4_K_M.gguf";

    /// <summary>GPU 加速启用阈值:专用显存 ≥4GB 才走 CUDA(以下属核显/亮机卡,CPU 更稳)</summary>
    private const long GpuEnableThresholdBytes = 4L * 1024 * 1024 * 1024;

    private static readonly Guid IidIdxgiFactory = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly ConfigService _config;
    private readonly AiModelLibraryService _library;   // 模型库:决定加载哪个用户模型
    private LLamaWeights? _weights;
    private ChatSession? _session;
    private ModelParams? _activeParams;   // 加载成功时的那套参数,对话新建 context 时复用(层数/主设备一致)
    private bool _cudaActive;
    private string _loadedModelPath = "";  // 当前已加载模型路径(状态展示 / 切换判定用)

    public AiEngineService(ConfigService config, AiModelLibraryService library) { _config = config; _library = library; }

    public EngineState State { get; private set; } = EngineState.Unloaded;
    public string StatusDetail { get; private set; } = "未加载";

    /// <summary>当前是否实际走上 CUDA 加速(本地 AI HTTP 接口 /v1/health 展示用)</summary>
    public bool GpuActive => _cudaActive;

    /// <summary>本机可用显存(MB):DXGI 优先、WMI 兜底,与加载判定同一口径(health 端点展示用)</summary>
    public static int UsableVramMb => (int)Math.Clamp(QueryMaxUsableVramBytes() / 1024 / 1024, 0, int.MaxValue);

    /// <summary>旧版内置模型的固定路径(向后兼容:模型库无活跃项时回退到此)</summary>
    public static string ModelPath => Path.Combine(AppPaths.Root, "models", ModelFileName);

    /// <summary>当前应加载的模型路径:取模型库活跃项,回退旧内置固定模型,均无则 null</summary>
    public string? ActiveModelPath => _library.ResolveLoadPath();

    /// <summary>当前已加载模型的文件名(未加载则空)</summary>
    public string LoadedModelName => string.IsNullOrEmpty(_loadedModelPath) ? "" : Path.GetFileName(_loadedModelPath);

    public bool ModelFileExists => File.Exists(ActiveModelPath ?? ModelPath);

    /// <summary>静态检查是否存在旧内置固定模型(保留兼容;导航已不再据此隐藏泡芙助理)</summary>
    public static bool ModelFileExistsStatic => File.Exists(ModelPath);

    /// <summary>CUDA12 原生后端是否随包部署(精简安装不含 cuda12 时为 false,强制走 CPU)</summary>
    public static bool CudaBackendPresent()
    {
        try
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            return File.Exists(Path.Combine(exeDir, "runtimes", "win-x64", "native", "cuda12", "llama.dll"));
        }
        catch { return false; }
    }

    /// <summary>确保模型加载完成(并发去重;已加载直接返回)。UI 线程调用安全</summary>
    public async Task<bool> EnsureLoadedAsync()
    {
        if (State == EngineState.Ready) return true;
        if (State == EngineState.Loading)
        {
            // 等待已在进行的加载完成
            await _loadGate.WaitAsync();
            _loadGate.Release();
            return State == EngineState.Ready;
        }

        await _loadGate.WaitAsync();
        try
        {
            if (State == EngineState.Ready) return true;
            string? path = ActiveModelPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                State = EngineState.NoModel;
                StatusDetail = "尚未导入模型:请在泡芙助理页面把 .gguf 模型拖入「请将模型拖入此处」区域";
                App.WriteAppLog("[AI] 无可用模型(用户尚未导入,models 目录也无内置模型)");
                return false;
            }

            State = EngineState.Loading;
            StatusDetail = $"正在加载模型:{Path.GetFileName(path)} …";
            App.WriteAppLog($"[AI] 开始加载模型:{path}");

            bool ok = await Task.Run(() => LoadCore(path));
            if (!ok && State != EngineState.NoModel)
            {
                State = EngineState.Error;
            }
            return State == EngineState.Ready;
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>静默卸载模型(游戏启动后 AI 退场专用):释放权重/上下文,归还显存与内存,
    /// 之后不再占用任何 GPU 性能与内存;重开泡芙助理页时 EnsureLoadedAsync 按需自动重载,
    /// 全程无需用户干预。reason 仅用于日志审计</summary>
    public async Task UnloadAsync(string reason)
    {
        await _loadGate.WaitAsync();
        try
        {
            if (_weights == null && State == EngineState.Unloaded) return;
            DisposeModel();
            State = EngineState.Unloaded;
            StatusDetail = "已静默(模型已卸载,显存与内存已释放)";
            App.WriteAppLog($"[AI] 静默卸载模型:{reason}");
        }
        finally { _loadGate.Release(); }
        // 权重虽已 Dispose,托管侧残留仍占进程内存;主动回收一次,坐实「不占用内存」
        try { GC.Collect(); } catch { }
    }

    /// <summary>重新加载模型(设置页切换 GPU 加速开关后调用)。会先释放旧模型再按新设置加载</summary>
    public async Task<bool> ReloadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            DisposeModel();
            State = EngineState.Unloaded;
            StatusDetail = "未加载";
        }
        finally { _loadGate.Release(); }
        return await EnsureLoadedAsync();
    }

    /// <summary>切换到指定模型:卸载当前 → 设为活跃 → 重新加载(泡芙助理「切换」按钮调用)</summary>
    public async Task<bool> SwitchModelAsync(string modelId)
    {
        await _loadGate.WaitAsync();
        try
        {
            DisposeModel();
            State = EngineState.Unloaded;
            StatusDetail = "未加载";
        }
        finally { _loadGate.Release(); }
        _library.SetActive(modelId);
        return await EnsureLoadedAsync();
    }

    /// <summary>加载核心:GPU 加速开关遵循(默认开,部署了 CUDA 后端时优先全层卸载、显存全量利用),失败自动回退 CPU</summary>
    private bool LoadCore(string path)
    {
        // 前置自检:文件可读性 + GGUF 魔数。
        // 原生层打不开文件时只丢一句笼统的 Failed to load model,1 秒内秒败的场景
        // (杀软扫描占用/下载或拷贝损坏)在这里精确区分,避免用户无从下手
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[4];
            int read = 0;
            while (read < 4) { int n = fs.Read(magic.Slice(read)); if (n <= 0) break; read += n; }
            if (read < 4 || magic[0] != (byte)'G' || magic[1] != (byte)'G' || magic[2] != (byte)'U' || magic[3] != (byte)'F')
            {
                State = EngineState.Error;
                StatusDetail = "模型文件已损坏(GGUF 魔数无效),请重新下载或重新拖入该模型";
                App.WriteAppLog($"[AI] 模型文件魔数无效(下载/拷贝损坏?):{path}");
                return false;
            }
        }
        catch (Exception ex)
        {
            State = EngineState.Error;
            StatusDetail = $"模型文件无法读取(可能被杀毒软件扫描占用,或拷贝不完整):{ex.Message}";
            App.WriteAppLog($"[AI] 模型文件前置自检失败:{path} -> {ex}");
            return false;
        }

        long vramBytes = QueryMaxUsableVramBytes();
        long fileSize = 0;
        try { fileSize = new FileInfo(path).Length; } catch { }
    
        bool gpuPref = _config.Config.AiGpuAcceleration;   // 设置页开关,默认开
        bool cudaDeployed = CudaBackendPresent();          // 精简安装不含 cuda12 原生库 → 强制 CPU
        bool gpuUsable = gpuPref && cudaDeployed && vramBytes >= GpuEnableThresholdBytes;   // 无 CUDA 后端或 <4GB 显存直接 CPU
        int gpuLayers = gpuUsable ? 999 : 0;                       // 全层卸载,显存有多少用多少
        if (gpuPref && !cudaDeployed)
            App.WriteAppLog("[AI] 未部署 CUDA12 原生后端(精简安装),泡芙助理走 CPU 推理");
    
        // 占用核算(日志可审计):权重 + 计算缓冲 + KV(qwen3-4b 36 层,ctx 3072,f16 KV ≈ 380MB)
        long estGpuBytes = gpuUsable ? fileSize + 300 * 1024 * 1024 + 380 * 1024 * 1024 : 0;
        App.WriteAppLog($"[AI] 硬件探测:可用显存={vramBytes / 1024 / 1024}MB,GPU加速={(gpuPref ? "开" : "关(强制 CPU)")},模式={(gpuUsable ? "CUDA 全层卸载(显存全量利用)" : "CPU")},估算占用={estGpuBytes / 1024 / 1024}MB");
    
        // CUDA 原生库必须在任何加载之前显式固定(LLamaSharp 自动检测只在根目录找 cudart,
        // 找不到就认定无 CUDA;WithAutoFallback 保证指定路径加载失败时回退默认 CPU 库)
        if (gpuUsable) TryPinCudaNativeLibrary();

        long vramBeforeMb = gpuUsable ? QueryNvidiaSmiVramUsedMb() : 0;
        var gpuParams = BuildModelParams(gpuLayers, path);
        _activeParams = gpuParams;
        App.WriteAppLog($"[AI] 生效推理参数:GpuLayerCount={gpuParams.GpuLayerCount} MainGpu={gpuParams.MainGpu} FlashAttention={gpuParams.FlashAttention} ContextSize={gpuParams.ContextSize}(MainGpu=0 锁定 CUDA0 主设备)");
        try
        {
            _weights = LLamaWeights.LoadFromFile(gpuParams);
            var ctx = _weights.CreateContext(gpuParams);
            _session = new ChatSession(new InteractiveExecutor(ctx));
            _cudaActive = gpuUsable;
            _loadedModelPath = path;
            State = EngineState.Ready;
            StatusDetail = gpuUsable
                ? $"就绪 · CUDA 加速 · 显存全量利用(实际约 {estGpuBytes / 1024 / 1024}MB)"
                : gpuPref
                    ? "就绪 · CPU 模式(未检测到 ≥4GB 显存的独立显卡)"
                    : "就绪 · CPU 模式(设置中已关闭 GPU 加速)";
            App.WriteAppLog($"[AI] 模型加载完成:{StatusDetail}");

            // CUDA 模式加载后实测验证:WithAutoFallback 在原生库加载失败时会静默回退 CPU 库
            // 且不抛异常,仅凭「加载成功」无法确认真的吃到了显存,必须用 nvidia-smi 前后差值核验
            if (gpuUsable) VerifyCudaActuallyActive(vramBeforeMb);
            return true;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[AI] CUDA 加载失败,回退 CPU:{ex.Message}");
            DisposeModel();
    
            if (!gpuUsable)
            {
                State = EngineState.Error;
                StatusDetail = $"模型加载失败:{ex.Message}";
                return false;
            }
    
            // CPU 兜底(老电脑/驱动缺失场景):原生库已加载不可切换,但 gpuLayers=0 时
            // 同一套库会自动把张量与计算全部放在 CPU,等效纯 CPU 推理
            try
            {
                var cpuParams = BuildModelParams(0, path);
                _activeParams = cpuParams;
                _weights = LLamaWeights.LoadFromFile(cpuParams);
                var ctx = _weights.CreateContext(cpuParams);
                _session = new ChatSession(new InteractiveExecutor(ctx));
                _cudaActive = false;
                _loadedModelPath = path;
                State = EngineState.Ready;
                StatusDetail = "就绪 · CPU 模式(CUDA 初始化失败已自动回退)";
                App.WriteAppLog("[AI] CPU 兜底加载成功");
                return true;
            }
            catch (Exception cpuEx)
            {
                State = EngineState.Error;
                StatusDetail = $"模型加载失败:{cpuEx.Message}。请重新下载或重新拖入模型;若杀毒软件正在扫描该文件,等扫描结束后重试";
                App.WriteAppLog($"[AI] CPU 加载也失败:{cpuEx}");
                return false;
            }
        }
    }

    private static ModelParams BuildModelParams(int gpuLayers, string path) => new(path)
    {
        ContextSize = 3072,
        GpuLayerCount = gpuLayers,
        MainGpu = 0,   // 锁定 CUDA0 主设备,禁止自动挑选(防核显/多卡误选)
        FlashAttention = gpuLayers > 0,
        Threads = Math.Max(4, Environment.ProcessorCount / 2),
        BatchSize = 512,
    };

    /// <summary>CUDA 模式加载完成后的显存实测核验:加载前后 nvidia-smi 显存占用差值
    /// 必须明显上涨(模型权重+KV+计算缓冲 ≈ 3GB),否则说明原生库静默回退到了 CPU 库,
    /// 此时必须把状态改为 CPU 并显著日志,绝不允许「标着 CUDA、实际 CPU」的假加速</summary>
    private void VerifyCudaActuallyActive(long vramBeforeMb)
    {
        long vramAfterMb = QueryNvidiaSmiVramUsedMb();
        if (vramBeforeMb < 0 || vramAfterMb < 0)
        {
            App.WriteAppLog("[AI] nvidia-smi 不可用,无法实测核验显存占用(以 CUDA 加载结果为准)");
            return;
        }
        long deltaMb = vramAfterMb - vramBeforeMb;
        App.WriteAppLog($"[AI] 显存实测:加载前={vramBeforeMb}MB 加载后={vramAfterMb}MB 增量={deltaMb}MB");
        if (deltaMb >= 1500)
        {
            App.WriteAppLog("[AI] 核验通过:CUDA 真实吃到显存,加速生效");
            return;
        }

        // 显存没有明显上涨 → 原生库静默回退 CPU(WithAutoFallback 不抛异常)
        _cudaActive = false;
        StatusDetail = "就绪 · CPU 模式(CUDA 原生库加载失败已静默回退,详见日志)";
        App.WriteAppLog("[AI] ⚠ 核验失败:显存增量不足 1500MB,判定 CUDA 未生效(原生库静默回退 CPU)。" +
            "请检查 runtimes\\win-x64\\native\\cuda12 目录是否完整(需 llama.dll/mtmd.dll/ggml*.dll/cudart64_12.dll/cublas64_12.dll/cublasLt64_12.dll)");
    }

    /// <summary>调用 nvidia-smi 读取全部 N 卡合计显存占用(MB);不可用时返回 -1</summary>
    private static long QueryNvidiaSmiVramUsedMb()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return -1;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return -1; }
            long total = 0;
            foreach (var line in output.Split('\n'))
            {
                string s = line.Trim();
                if (s.Length > 0 && long.TryParse(s, out long mb)) total += mb;
            }
            return total;
        }
        catch { return -1; }
    }

    /// <summary>显式固定 CUDA 原生库(必须在任何原生库加载之前调用)。
    /// LLamaSharp 0.27 的自动检测只在程序根目录找 cudart64_12.dll,找不到就认定无 CUDA
    /// (实测 RTX5060 也因此被误判),故直接指定 cuda12 目录的 llama.dll/mtmd.dll;
    /// WithAutoFallback 保证指定路径加载失败(驱动缺失等)时自动回退默认 CPU 库。
    /// 注意:单文件发布的 AppContext.BaseDirectory 指向 Temp 自解压目录,必须用
    /// Environment.ProcessPath 取 exe 真实目录(松散 runtimes 在 exe 旁)</summary>
    private static void TryPinCudaNativeLibrary()
    {
        try
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string cudaDir = Path.Combine(exeDir, "runtimes", "win-x64", "native", "cuda12");
            string llamaDll = Path.Combine(cudaDir, "llama.dll");
            string mtmdDll = Path.Combine(cudaDir, "mtmd.dll");
            if (!File.Exists(llamaDll) || !File.Exists(mtmdDll))
            {
                App.WriteAppLog($"[AI] cuda12 原生库不完整(缺 llama.dll 或 mtmd.dll),走默认库:{cudaDir}");
                return;
            }
            // CUDA 运行时三件套 + ggml 依赖缺一即静默掉 CPU,这里先点名缺失文件便于排查
            string[] required = ["ggml.dll", "ggml-base.dll", "ggml-cpu.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"];
            var missing = required.Where(f => !File.Exists(Path.Combine(cudaDir, f))).ToArray();
            if (missing.Length > 0)
                App.WriteAppLog($"[AI] ⚠ cuda12 目录缺少依赖文件:{string.Join(", ", missing)}(加载时将静默回退 CPU)");
            LLama.Native.NativeLibraryConfig.All
                .WithLibrary(llamaDll, mtmdDll)
                .WithAutoFallback(true);
            App.WriteAppLog($"[AI] 已固定 CUDA 原生库目录:{cudaDir}");
        }
        catch (Exception ex)
        {
            // 库已加载(ReloadAsync 场景)或配置异常:不阻断,走默认库
            App.WriteAppLog($"[AI] CUDA 原生库固定跳过:{ex.Message}");
        }
    }

    /// <summary>流式对话:逐 token 回调 onToken;返回完整回复。ct 取消时抛出 OperationCanceledException。
    /// 每次调用新建独立 ChatSession:_session 为单例且内部维护对话状态,复用会导致
    /// 「Cannot add a system message after another message」异常(第二轮起再塞 system 即冲突),
    /// 同时隔离意图解析与闲聊的上下文,避免互相污染</summary>
    public async Task<string> ChatAsync(ChatHistory history, Action<string> onToken, CancellationToken ct)
    {
        var weights = _weights;
        if (State != EngineState.Ready || weights == null)
            throw new InvalidOperationException("模型未就绪,请先加载");

        // 每次调用新建独立 context/session:复用全局 _session 会累积对话状态,第二轮起再塞
        // system 消息直接报「Cannot add a system message after another message」;
        // 独立会话同时隔离意图解析与闲聊的上下文,避免互相污染。
        // using 声明序按创建逆序释放(session → executor → ctx)
        using var ctx = weights.CreateContext(_activeParams
            ?? throw new InvalidOperationException("模型参数丢失,请重新加载模型"));
        var session = new ChatSession(new InteractiveExecutor(ctx));
        var param = new InferenceParams
        {
            MaxTokens = 1200,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.7f,
                RepeatPenalty = 1.15f,     // 抑制尾部 )))))) 之类同 token 重复失控
                PenaltyCount = 128,        // 重复惩罚回看窗口
                FrequencyPenalty = 0.2f,
                PresencePenalty = 0.2f,
            },
        };

        var sb = new StringBuilder();
        await foreach (var token in session.ChatAsync(history, param, ct))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(token);
            onToken(token);
        }
        return sb.ToString();
    }

    /// <summary>探测最大可用显存(字节):优先独立显存,独显不足时把共享系统内存的一半计入口径
    /// (核显/APU 场景专用显存报 0 但实际可用共享内存);DXGI 枚举失败或返回 0 时转 WMI 兜底
    /// (WMI AdapterRAM uint32 在 4GB 处截断,只能做阈值判定,故仅作兜底)</summary>
    private static long QueryMaxUsableVramBytes()
    {
        long dxgi = 0;
        try { dxgi = QueryViaDxgi(); }
        catch (Exception ex)
        {
            App.WriteAppLog($"[AI] DXGI 显存探测异常,转 WMI 阈值判定:{ex.Message}");
        }
        if (dxgi > 0) return dxgi;
        // DXGI 枚举失败或返回 0(如虚拟显示适配器干扰、槽位不匹配)同样转 WMI,不能静默当作无独显
        long wmi = QueryViaWmiThreshold();
        App.WriteAppLog($"[AI] DXGI 枚举未得到可用显存,转 WMI 阈值判定:结果={wmi / 1024 / 1024}MB");
        return wmi;
    }

    /// <summary>WMI 兜底:AdapterRAM 是 uint32,≥4GB 的卡一律报 0xFFFFFFFF,
    /// 刚好够做「是否 ≥4GB」阈值判定(无法区分具体容量,但本策略只需阈值)</summary>
    private static long QueryViaWmiThreshold()
    {
        try
        {
            long max = 0;
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT AdapterRAM FROM Win32_VideoController");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["AdapterRAM"] is uint ram && ram > max) max = ram;
                }
            }
            return max >= 0xFFF00000 ? 4L * 1024 * 1024 * 1024 : max;   // uint32 封顶即视为 ≥4GB
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[AI] WMI 显存探测也失败(按无独显处理):{ex.Message}");
            return 0;
        }
    }

    private static unsafe long QueryViaDxgi()
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            Guid iid = IidIdxgiFactory;   // static readonly 不能直接作 ref/out,先取局部副本
            int hr = CreateDXGIFactory1(ref iid, out factory);
            if (hr != 0 || factory == IntPtr.Zero) return 0;

            long max = 0;
            var factoryVtbl = Marshal.ReadIntPtr(factory);
            // IDXGIFactory1 虚表布局(注意 IDXGIObject 有 4 个方法,不是 3 个):
            //   IUnknown(0 QueryInterface / 1 AddRef / 2 Release)
            //   + IDXGIObject(3 GetPrivateData / 4 SetPrivateData / 5 SetPrivateDataInterface / 6 GetParent)
            //   + IDXGIFactory(7 EnumAdapters / 8 MakeWindowAssociation / 9 GetWindowAssociation /
            //     10 CreateSwapChain / 11 CreateSoftwareAdapter)
            //   + IDXGIFactory1(12 EnumAdapters1 / 13 IsCurrent)
            // 槽位记错会调错函数(gpudiag 实测:槽位 6 实际是 GetParent,枚举直接 E_NOINTERFACE)
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)
                Marshal.ReadIntPtr(factoryVtbl, 12 * IntPtr.Size);

            for (uint i = 0; i < 8; i++)
            {
                int hrEnum = enumAdapters1(factory, i, out IntPtr adapter);
                if (hrEnum != 0) break;
                try
                {
                    var adapterVtbl = Marshal.ReadIntPtr(adapter);
                    // IDXGIAdapter 虚表:IUnknown(0-2) + IDXGIObject(3-6) + 7 EnumOutputs / 8 GetDesc
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, out DxgiAdapterDesc, int>)
                        Marshal.ReadIntPtr(adapterVtbl, 8 * IntPtr.Size);
                    if (getDesc(adapter, out var desc) == 0)
                    {
                        // 独显取专用显存;核显/APU 专用显存为 0,把共享系统内存的一半计入口径
                        long usable = (long)desc.DedicatedVideoMemory;
                        if (usable < 256 * 1024 * 1024 && (long)desc.SharedSystemMemory > 0)
                            usable = (long)desc.SharedSystemMemory / 2;
                        App.WriteAppLog($"[AI] DXGI 适配器[{i}]:{desc.Description} 专用={(long)desc.DedicatedVideoMemory / 1024 / 1024}MB 共享={(long)desc.SharedSystemMemory / 1024 / 1024}MB 计入口径={usable / 1024 / 1024}MB");
                        if (usable > max) max = usable;
                    }
                }
                finally { Marshal.Release(adapter); }
            }
            return max;
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[AI] DXGI 显存探测失败:{ex.Message}");
            throw;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public IntPtr DedicatedVideoMemory;
        public IntPtr DedicatedSystemMemory;
        public IntPtr SharedSystemMemory;
        public long AdapterLuid;
    }

    private void DisposeModel()
    {
        try { _session = null; _weights?.Dispose(); } catch { }
        _weights = null;
        _activeParams = null;
        _loadedModelPath = "";
    }
}

/// <summary>路径显示帮助(避免日志里长路径刷屏)</summary>
internal static class Model
{
    public static string GetFileNameSafe(string path)
    {
        try { return Path.GetFileName(path); } catch { return path; }
    }
}
