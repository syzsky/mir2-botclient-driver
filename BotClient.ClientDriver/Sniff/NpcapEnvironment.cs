using System.Diagnostics;

namespace BotClient.ClientDriver.Sniff;

/// <summary>Npcap 探测结果。</summary>
public enum NpcapProbeStatus
{
    /// <summary>已安装、可直接抓包。</summary>
    Ready,

    /// <summary>本次刚由随包安装器装上（wpcap.dll 已就位，本进程可直接继续）。</summary>
    InstalledNow,

    /// <summary>未安装，且 exe 目录下没有可用的随包安装器。</summary>
    Missing,

    /// <summary>找到安装器但安装失败。</summary>
    InstallFailed,

    /// <summary>用户显式关闭了自动补装（--no-auto-install）。</summary>
    Disabled,

    /// <summary>非 Windows 平台。</summary>
    NotWindows,
}

/// <summary>探测结论。<paramref name="InstallerPath"/> 为本次使用/找到的安装器路径（可能为空）。</summary>
public readonly record struct NpcapProbeResult(
    NpcapProbeStatus Status,
    string Message,
    string? InstallerPath = null)
{
    /// <summary>是否可以立即开始抓包。</summary>
    public bool Ok => Status == NpcapProbeStatus.Ready;
}

/// <summary>
/// Npcap 运行环境探测 + 随包安装器自动补装。
///
/// 为什么不能真的"内置"：Npcap 是内核驱动（npcap.sys + 用户态 wpcap.dll/Packet.dll），
/// 驱动必须注册进系统才能被按名字加载，无法像普通 DLL 那样塞进单文件 exe。
/// 所以分发包采用 "BotClientDriverHost.exe + npcap\npcap-x.xx.exe" 的形式：
/// 首次运行由本类检测缺失 → 拉起随包安装器的**交互式向导**（免费版不支持 /S 静默安装，见 TryInstall 注释）
/// → 装完复检 wpcap.dll 后本进程直接继续，不需要"装完再重开一次"。
/// 用户体感就是"解压即用"，中间只过一次 UAC（宿主已声明 requireAdministrator）。
///
/// 合规提醒：Npcap 免费版允许自用与随程序分发原始安装器；若要作为商业产品对外 OEM 再分发，
/// 需向 Nmap Project 取得 OEM 授权。
/// </summary>
public static class NpcapEnvironment
{
    /// <summary>随包安装器候选子目录（相对宿主 exe 目录，空串表示 exe 同目录）。</summary>
    private static readonly string[] InstallerDirs = { "npcap", "redist", "tools", string.Empty };

    /// <summary>随包安装器文件名模式（按此顺序命中即用）。</summary>
    private static readonly string[] InstallerPatterns = { "npcap-*.exe", "npcap*.exe", "Npcap*.exe", "npcap*.msi" };

    /// <summary>静默安装超时（毫秒）：驱动安装通常 20~60 秒，这里留足余量。仅 OEM 版走这条路。</summary>
    private const int InstallTimeoutMs = 5 * 60 * 1000;

    /// <summary>交互式安装超时（毫秒）：要等用户点完向导，给 15 分钟。</summary>
    private const int InteractiveInstallTimeoutMs = 15 * 60 * 1000;

    /// <summary>只读探测：Npcap 的用户态库是否已就位（不发包、不安装、不改系统）。</summary>
    public static bool IsDriverPresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(win))
        {
            return false;
        }

        // ① Npcap 默认布局：System32\Npcap\wpcap.dll（x64 进程）
        // ② 勾了 WinPcap 兼容模式时还会在 System32 放一份 wpcap.dll（WinPcap 残留也认）
        // ③ 32 位视图一并认，避免"装了但位数不匹配"被误报成未安装，把真正的问题掩盖掉
        foreach (string dir in new[] { Path.Combine(win, "System32"), Path.Combine(win, "SysWOW64") })
        {
            if (File.Exists(Path.Combine(dir, "Npcap", "wpcap.dll")))
            {
                return true;
            }

            if (File.Exists(Path.Combine(dir, "wpcap.dll")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>当前 Npcap 状态的人话描述（自检报告/日志用）。</summary>
    public static string Describe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "非 Windows 平台（不支持）";
        }

        if (IsDriverPresent())
        {
            return "已安装";
        }

        return FindInstaller(AppContext.BaseDirectory) is { } p
            ? $"未安装（随包安装器: {Path.GetFileName(p)}）"
            : "未安装（无随包安装器）";
    }

    /// <summary>在 exe 同目录及候选子目录里找随包安装器；找不到返回 null。</summary>
    public static string? FindInstaller(string baseDir)
    {
        foreach (string sub in InstallerDirs)
        {
            string dir = string.IsNullOrEmpty(sub) ? baseDir : Path.Combine(baseDir, sub);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string pattern in InstallerPatterns)
            {
                try
                {
                    // 目录里可能同时躺了多个版本，取最新的那个
                    string? hit = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (hit != null)
                    {
                        return hit;
                    }
                }
                catch
                {
                    // 目录不可读（权限/占用）就跳过，继续找下一个候选
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 确保抓包环境可用：Npcap 缺失时（可选）用随包安装器补装（交互式向导，见 TryInstall）。
    /// 安装动作会写系统目录并注册内核驱动 —— 宿主已声明 requireAdministrator，可直接执行。
    /// </summary>
    public static NpcapProbeResult Probe(string baseDir, bool autoInstall = true, Action<string>? announce = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NpcapProbeResult(NpcapProbeStatus.NotWindows, "抓包与输入模拟仅支持 Windows。");
        }

        if (IsDriverPresent())
        {
            return new NpcapProbeResult(NpcapProbeStatus.Ready, "Npcap 已就绪");
        }

        string? installer = FindInstaller(baseDir);

        if (!autoInstall)
        {
            return new NpcapProbeResult(
                NpcapProbeStatus.Disabled,
                installer == null
                    ? "Npcap 未安装，且 exe 目录下没有随包安装器（自动补装已被 --no-auto-install 关闭）"
                    : $"Npcap 未安装（自动补装已被 --no-auto-install 关闭；随包安装器: {installer}）",
                installer);
        }

        if (installer == null)
        {
            return new NpcapProbeResult(
                NpcapProbeStatus.Missing,
                "Npcap 未安装，且 exe 同目录（含 npcap\\ 子目录）里没有 npcap-*.exe。"
                + "请到 https://npcap.com/#download 下载安装器后放到 exe 同目录，"
                + "或用 --install-npcap <安装器路径> 指定。");
        }

        // 免费版 Npcap 的安装器不认 /S（会弹框报 "Silent installation is only supported in Npcap OEM" 然后退出），
        // 所以这里明确告知用户"马上要弹向导、该怎么勾"，装完本进程直接继续，不需要重开。
        announce?.Invoke(
            "检测到 Npcap 未安装，现在弹出官方安装向导（免费版不支持静默安装，需要点完向导）。\n"
            + "        ① 一路 Next；② 遇到 “Install Npcap in WinPcap API-compatible Mode” 必须勾上；\n"
            + "        ③ 点 Install 等它装完即可 —— 本程序会自动继续，不用重开。");

        if (!TryInstall(installer, out string detail, interactive: true))
        {
            return new NpcapProbeResult(NpcapProbeStatus.InstallFailed, detail, installer);
        }

        return new NpcapProbeResult(
            NpcapProbeStatus.InstalledNow,
            $"Npcap 安装完成（{Path.GetFileName(installer)}），驱动已就位。",
            installer);
    }

    /// <summary>
    /// 安装 Npcap。
    ///
    /// 真机踩坑（已修）：免费版 Npcap 的安装器**只允许 OEM 版静默安装** —— 传 <c>/S</c> 会直接弹框
    /// "Silent installation is only supported in Npcap OEM" 并退出，宿主把这当失败处理，用户看到的就是"双击闪退"。
    /// 因此默认走交互式向导（<paramref name="interactive"/> = true）：由安装器自己弹 UI，用户勾完选项点 Install，
    /// 本进程等它结束再复检 wpcap.dll。安装完成后**本进程可继续**（wpcap.dll 是首次抓包时才 P/Invoke 加载的）。
    /// <paramref name="interactive"/> = false 时保留旧的 /S 静默路径，仅在确认是 OEM 包时才可用。
    /// MSI 的勾选项由包内默认值决定，这里只做 /qn 静默，故优先用官方 exe 安装器。
    /// </summary>
    public static bool TryInstall(string installerPath, out string detail, bool interactive = true)
    {
        detail = string.Empty;
        if (!File.Exists(installerPath))
        {
            detail = $"安装器不存在: {installerPath}";
            return false;
        }

        bool isMsi = installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        string fileName;
        string arguments;

        if (isMsi)
        {
            fileName = "msiexec.exe";
            arguments = interactive
                ? $"/i \"{installerPath}\""
                : $"/i \"{installerPath}\" /qn /norestart";
        }
        else
        {
            fileName = installerPath;
            // 交互式不带任何参数：免得免费版对未知参数直接报错，勾选项交给用户在向导里点。
            // 静默才用 /S /winpcap_mode=yes（保证 SharpPcap 依赖的 wpcap.dll 可用）。
            arguments = interactive ? string.Empty : "/S /winpcap_mode=yes";
        }

        // 交互式要显示向导，交给 shell 启动（同时也能正常触发 UAC 提权）。
        int timeoutMs = interactive ? InteractiveInstallTimeoutMs : InstallTimeoutMs;

        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = interactive,
                CreateNoWindow = !interactive,
            };

            using var p = Process.Start(psi);
            if (p == null)
            {
                detail = "无法启动安装器进程";
                return false;
            }

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                detail = $"安装向导超时未结束（>{timeoutMs / 60000} 分钟），已终止";
                return false;
            }

            // 成败以驱动是否真的就位为准：向导可能有"已安装同版本"等非 0 退出码，但只要 wpcap.dll 在就算成功。
            if (!IsDriverPresent())
            {
                detail = p.ExitCode != 0
                    ? $"安装向导已关闭（退出码 {p.ExitCode}），但没有检测到 wpcap.dll —— 可能是中途取消了，"
                      + "或安装时没勾 “WinPcap API-compatible Mode”。可手动双击安装器重试一次。"
                    : "安装向导已结束，但没有检测到 wpcap.dll —— 请确认勾选了 “WinPcap API-compatible Mode”，"
                      + "或检查是否被安全软件拦截。";
                return false;
            }

            detail = interactive ? "安装完成（wpcap.dll 已就位）" : "静默安装成功";
            return true;
        }
        catch (Exception ex)
        {
            detail = interactive
                ? $"拉起安装向导异常: {ex.Message}（可手动双击 {installerPath} 安装）"
                : $"静默安装异常: {ex.Message}";
            return false;
        }
    }
}
