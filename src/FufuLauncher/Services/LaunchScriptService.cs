// Copyright © FufuLauncher
//
// 实例前后置脚本服务(Celestial-7):
// 每个游戏版本可以配一条"启动前脚本"和一条"退出后脚本",并有一个总开关一键关掉整套功能。
//
// 实现要点:
// 1. 脚本一律独立进程执行,绝不在启动器进程内 eval;超时强制结束整棵进程树,不卡住启动/退出流程;
// 2. 支持 .bat / .cmd / .ps1 / .exe 以及"任意命令 + 参数"写法,自动挑选宿主解释器;
// 3. 向脚本注入一组 FUFU_* 环境变量(实例路径、游戏版本、退出码等),脚本可直接取用;
// 4. 前置脚本失败默认"警告但继续启动"(用户可要求硬失败);后置脚本失败只记日志;
// 5. 全部输出(stdout + stderr)抓回来,失败时原样展示,方便用户自己看脚本哪儿写错了。

using System.Diagnostics;
using System.IO;
using System.Text;

namespace FufuLauncher.Services;

/// <summary>脚本执行结果</summary>
public sealed class ScriptRunResult
{
    /// <summary>是否真的执行了(false = 总开关关闭 / 脚本为空 / 文件不存在)</summary>
    public bool Executed { get; set; }
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    /// <summary>是否因超时被强制结束</summary>
    public bool TimedOut { get; set; }
    public string Output { get; set; } = "";
    public string Message { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
    /// <summary>被跳过(未执行)的原因</summary>
    public string? SkippedReason { get; set; }

    /// <summary>给 UI 展示的一段话</summary>
    public string Display
    {
        get
        {
            if (!Executed) return SkippedReason ?? "脚本未执行。";
            if (TimedOut) return $"脚本执行超时,已被强制结束(耗时 {Elapsed.TotalSeconds:0.0} 秒)。";
            return Success
                ? $"脚本执行完成(退出码 {ExitCode},耗时 {Elapsed.TotalSeconds:0.0} 秒)。"
                : $"脚本执行失败(退出码 {ExitCode})。";
        }
    }
}

/// <summary>脚本上下文(注入给脚本的环境变量)</summary>
public sealed class ScriptContext
{
    public string InstanceId { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string InstanceDir { get; set; } = "";
    public string ModsDir { get; set; } = "";
    public string SavesDir { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string ModLoader { get; set; } = "";
    public string JavaPath { get; set; } = "";
    /// <summary>后置脚本专用:游戏退出码</summary>
    public int GameExitCode { get; set; }
    /// <summary>后置脚本专用:本次启动是否成功</summary>
    public bool LaunchSuccess { get; set; }
}

public sealed class LaunchScriptService
{
    private readonly InstanceExtrasService _extras;
    private readonly InstanceService _instances;

    public LaunchScriptService(InstanceExtrasService extras, InstanceService instances)
    {
        _extras = extras;
        _instances = instances;
    }

    /// <summary>读取实例的脚本配置</summary>
    public (bool Enabled, string Pre, string Post, int Timeout) GetConfig(string instanceId)
        => _extras.ScriptsOf(instanceId);

    /// <summary>保存实例的脚本配置</summary>
    public void SaveConfig(string instanceId, bool enabled, string pre, string post, int timeoutSeconds)
        => _extras.SaveScripts(instanceId, enabled, pre, post, timeoutSeconds);

    /// <summary>总开关是否打开(关闭时前后置脚本一律不执行)</summary>
    public bool IsEnabled(string instanceId) => _extras.ScriptsOf(instanceId).Enabled;

    /// <summary>
    /// 校验脚本命令:返回 (是否可用, 中文提示)。仅做"能不能跑"的静态检查,不执行。
    /// </summary>
    public (bool Ok, string Message) Validate(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return (true, "脚本为空,启动时会被跳过。");
        var (exe, args) = SplitCommand(commandLine);
        if (exe.Length == 0) return (false, "脚本路径是空的,请填一个 .bat / .ps1 / .exe 的完整路径。");

        // 带引号的路径先剥引号再判存在性
        string probe = exe.Trim('"');
        if (File.Exists(probe)) return (true, $"脚本文件已找到:{Path.GetFileName(probe)}{(args.Length > 0 ? " " + args : "")}");

        // 不是文件路径 → 当作"命令行指令"(如 cmd /c echo hi、notepad 等),交给系统解析
        if (probe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return (false, $"脚本路径「{Trunc(probe)}」里含有非法字符。");
        return (true, $"「{Trunc(probe)}」不是磁盘上的文件,将按系统命令执行(需自行确保命令存在)。");
    }

    /// <summary>执行游戏启动前脚本</summary>
    public async Task<ScriptRunResult> RunPreLaunchAsync(string instanceId, CancellationToken ct = default)
    {
        var (enabled, pre, _, timeout) = _extras.ScriptsOf(instanceId);
        if (!enabled)
            return new ScriptRunResult { Executed = false, SkippedReason = "脚本功能总开关是关的,已跳过。" };
        if (string.IsNullOrWhiteSpace(pre))
            return new ScriptRunResult { Executed = false, SkippedReason = "没有设置启动前脚本。" };

        var ctx = BuildContext(instanceId);
        App.WriteAppLog($"[脚本] ▶ 前置脚本开始:{pre}(超时 {timeout}s)");
        var res = await RunAsync(pre, ctx.InstanceDir, timeout, ctx, ct).ConfigureAwait(false);
        App.WriteAppLog($"[脚本] {(res.Success ? "✓" : "✗")} 前置脚本结束:{res.Display}");
        return res;
    }

    /// <summary>执行游戏退出后脚本</summary>
    public async Task<ScriptRunResult> RunPostExitAsync(string instanceId, int gameExitCode, bool launchSuccess,
                                                       CancellationToken ct = default)
    {
        var (enabled, _, post, timeout) = _extras.ScriptsOf(instanceId);
        if (!enabled)
            return new ScriptRunResult { Executed = false, SkippedReason = "脚本功能总开关是关的,已跳过。" };
        if (string.IsNullOrWhiteSpace(post))
            return new ScriptRunResult { Executed = false, SkippedReason = "没有设置退出后脚本。" };

        var ctx = BuildContext(instanceId);
        ctx.GameExitCode = gameExitCode;
        ctx.LaunchSuccess = launchSuccess;
        App.WriteAppLog($"[脚本] ▶ 后置脚本开始:{post}(退出码 {gameExitCode},超时 {timeout}s)");
        var res = await RunAsync(post, ctx.InstanceDir, timeout, ctx, ct).ConfigureAwait(false);
        App.WriteAppLog($"[脚本] {(res.Success ? "✓" : "✗")} 后置脚本结束:{res.Display}");
        return res;
    }

    // ==================== 执行 ====================

    /// <summary>
    /// 执行一条脚本命令:自动挑选宿主、注入环境变量、抓输出、超时杀进程树。
    /// 绝不抛异常 —— 任何失败都转成 ScriptRunResult,保证启动流程不被脚本拖死。
    /// </summary>
    public async Task<ScriptRunResult> RunAsync(string commandLine, string workDir, int timeoutSeconds,
                                               ScriptContext ctx, CancellationToken ct = default)
    {
        var res = new ScriptRunResult();
        var sw = Stopwatch.StartNew();
        try
        {
            var (exe, args) = SplitCommand(commandLine);
            if (exe.Length == 0)
            {
                res.SkippedReason = "脚本路径为空。";
                return res;
            }

            // .ps1 必须走 powershell 宿主;.bat/.cmd 走 cmd /c;其余按可执行文件直接起
            string realExe = exe;
            string realArgs = args;
            string probe = exe.Trim('"');
            string ext = Path.GetExtension(probe).ToLowerInvariant();
            bool fileExists = File.Exists(probe);

            if (fileExists && ext == ".ps1")
            {
                realExe = "powershell.exe";
                realArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{probe}\"{(args.Length > 0 ? " " + args : "")}";
            }
            else if (fileExists && (ext == ".bat" || ext == ".cmd"))
            {
                realExe = "cmd.exe";
                realArgs = $"/c \"\"{probe}\"{(args.Length > 0 ? " " + args : "")}\"";
            }
            else if (fileExists)
            {
                realExe = probe;
            }
            else
            {
                // 不是磁盘文件:整条命令交给 cmd /c 解析(允许 "notepad"、"echo hi" 这类写法)
                realExe = "cmd.exe";
                realArgs = "/c " + commandLine;
            }

            if (!string.IsNullOrEmpty(workDir) && !Directory.Exists(workDir)) workDir = AppPaths.Root;

            var psi = new ProcessStartInfo
            {
                FileName = realExe,
                Arguments = realArgs,
                WorkingDirectory = string.IsNullOrEmpty(workDir) ? AppPaths.Root : workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            InjectEnv(psi, ctx);

            var sbOut = new StringBuilder();
            var sbErr = new StringBuilder();
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sbOut) sbOut.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sbErr) sbErr.AppendLine(e.Data); };

            if (!proc.Start())
            {
                res.Message = "脚本进程启动失败。";
                return res;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            res.Executed = true;

            int timeoutMs = Math.Clamp(timeoutSeconds, 5, 3600) * 1000;
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                res.TimedOut = !ct.IsCancellationRequested;   // ct 取消(用户主动)不算超时
                KillTree(proc);
                // 给子进程一点收尾时间,避免僵尸句柄
                try { proc.WaitForExit(1500); } catch { }
            }

            sw.Stop();
            res.Elapsed = sw.Elapsed;
            res.ExitCode = SafeExitCode(proc);
            string outText, errText;
            lock (sbOut) outText = sbOut.ToString();
            lock (sbErr) errText = sbErr.ToString();
            res.Output = (outText + (errText.Length > 0 ? "\n[stderr]\n" + errText : "")).Trim();
            if (res.Output.Length > 4000) res.Output = res.Output[..4000] + "…(输出过长已截断)";
            res.Success = !res.TimedOut && res.ExitCode == 0;
            res.Message = res.Success ? "脚本执行完成。"
                        : res.TimedOut ? $"脚本超过 {timeoutSeconds} 秒还没结束,已被强制停止。"
                        : $"脚本返回退出码 {res.ExitCode}。";
            return res;
        }
        catch (Exception ex)
        {
            sw.Stop();
            res.Elapsed = sw.Elapsed;
            res.Success = false;
            res.Message = "脚本执行出错:" + ex.Message;
            App.WriteAppLog($"[脚本] ✗ 执行异常:{ex}");
            return res;
        }
    }

    private static int SafeExitCode(Process proc)
    {
        try { return proc.HasExited ? proc.ExitCode : -1; }
        catch { return -1; }
    }

    /// <summary>结束整棵进程树(脚本可能又拉起别的进程)</summary>
    private static void KillTree(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[脚本] 结束进程树失败,退回单进程结束:{ex.Message}");
            try { proc.Kill(); } catch { }
        }
    }

    /// <summary>注入 FUFU_* 环境变量(脚本里用 %FUFU_INSTANCE_DIR% / $env:FUFU_INSTANCE_DIR 取)</summary>
    private static void InjectEnv(ProcessStartInfo psi, ScriptContext ctx)
    {
        if (ctx == null) return;
        psi.EnvironmentVariables["FUFU_INSTANCE_ID"] = ctx.InstanceId ?? "";
        psi.EnvironmentVariables["FUFU_INSTANCE_NAME"] = ctx.InstanceName ?? "";
        psi.EnvironmentVariables["FUFU_INSTANCE_DIR"] = ctx.InstanceDir ?? "";
        psi.EnvironmentVariables["FUFU_MODS_DIR"] = ctx.ModsDir ?? "";
        psi.EnvironmentVariables["FUFU_SAVES_DIR"] = ctx.SavesDir ?? "";
        psi.EnvironmentVariables["FUFU_MC_VERSION"] = ctx.McVersion ?? "";
        psi.EnvironmentVariables["FUFU_MOD_LOADER"] = ctx.ModLoader ?? "";
        psi.EnvironmentVariables["FUFU_JAVA_PATH"] = ctx.JavaPath ?? "";
        psi.EnvironmentVariables["FUFU_GAME_EXIT_CODE"] = ctx.GameExitCode.ToString();
        psi.EnvironmentVariables["FUFU_LAUNCH_SUCCESS"] = ctx.LaunchSuccess ? "1" : "0";
    }

    private ScriptContext BuildContext(string instanceId)
    {
        var inst = _instances.Instances.FirstOrDefault(i => i.Id == instanceId);
        return new ScriptContext
        {
            InstanceId = instanceId,
            InstanceName = inst?.Name ?? instanceId,
            InstanceDir = _instances.GetInstanceDir(instanceId),
            ModsDir = _instances.GetModsDir(instanceId),
            SavesDir = _instances.GetSavesDir(instanceId),
            McVersion = inst?.VersionId ?? "",
            ModLoader = inst?.ModLoader ?? "",
            JavaPath = inst?.JavaPath ?? ""
        };
    }

    // ==================== 命令解析 ====================

    /// <summary>
    /// 拆出可执行文件与参数:支持
    ///   "D:\a b\run.bat" --flag        (带引号路径)
    ///   D:\ab\run.bat --flag           (无空格路径)
    ///   echo hello                     (纯命令)
    /// </summary>
    public static (string Exe, string Args) SplitCommand(string commandLine)
    {
        string s = (commandLine ?? "").Trim();
        if (s.Length == 0) return ("", "");
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end < 0) return (s, "");
            return (s[1..end], s[(end + 1)..].Trim());
        }
        // 无引号:第一个空白前的部分当可执行文件;若该部分不是已存在文件,
        // 尝试逐段拼接路径(应对 D:\my dir\run.bat 这种没加引号的带空格路径)
        var parts = SplitOutsideQuotes(s);
        for (int take = parts.Count - 1; take >= 1; take--)
        {
            string candidate = string.Join(' ', parts.Take(take));
            if (File.Exists(candidate))
                return (candidate, string.Join(' ', parts.Skip(take)));
        }
        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    /// <summary>按空白切分,尊重双引号</summary>
    private static List<string> SplitOutsideQuotes(string s)
    {
        var list = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false;
        foreach (char c in s)
        {
            if (c == '"') { inQuote = !inQuote; cur.Append(c); continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    private static string Trunc(string s) => s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>环境变量说明(供 UI 展示"脚本里能用哪些变量")</summary>
    public static readonly (string Name, string Desc)[] EnvVarDocs =
    {
        ("FUFU_INSTANCE_ID",   "游戏版本内部标识"),
        ("FUFU_INSTANCE_NAME", "游戏版本名称"),
        ("FUFU_INSTANCE_DIR",  "游戏版本目录(即游戏工作目录)"),
        ("FUFU_MODS_DIR",      "该版本的模组文件夹"),
        ("FUFU_SAVES_DIR",     "该版本的存档文件夹"),
        ("FUFU_MC_VERSION",    "游戏版本号"),
        ("FUFU_MOD_LOADER",    "模组加载器(Fabric / Forge / 空)"),
        ("FUFU_JAVA_PATH",     "该版本指定的 java.exe 路径"),
        ("FUFU_GAME_EXIT_CODE", "游戏退出码(仅退出后脚本有值)"),
        ("FUFU_LAUNCH_SUCCESS", "本次启动是否成功,1 / 0(仅退出后脚本有值)")
    };
}
