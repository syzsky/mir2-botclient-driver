using System.Collections.Concurrent;
using BotClient.Net;

namespace BotClient.ClientDriver.Input;

/// <summary>
/// 兜底通道：实现 Core 的 <see cref="IUpstreamSink"/>，接收**没有被 Driver 接管**的上行 payload。
///
/// 它存在的意义不是"发出去"，而是**把漏洞暴露出来**。
/// C 方案的补丁方式是逐个 Send* 方法加分流，漏掉一个方法就意味着：Bot 以为自己在打怪，
/// 实际上客户端什么都没做，而在旧逻辑下那个包会被真的写进 socket —— 那就等于
/// 在真客户端的连接上注入了第二个角色的动作，是最危险的情况。
///
/// 所以这里的策略是：
///   • **绝不转发字节**（不实现任何 socket 写入）；
///   • 解析出命令码，计数、记录、去重汇总；
///   • 启动后把统计暴露给 UI，由人确认"还有哪些命令没接上"。
/// 这是把"静默失效"变成"可见的待办"。
/// </summary>
public sealed class ClientInputBridge : IUpstreamSink
{
    private readonly ConcurrentDictionary<ushort, long> _droppedByCmd = new();

    public event Action<string>? Log;

    /// <summary>驱动已附着时，这里永远是"连接可用"的 —— BotRuntime 的 IsConnected 依赖它。</summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>被丢弃（未接管）的命令码统计。</summary>
    public IReadOnlyDictionary<ushort, long> DroppedCommands => _droppedByCmd;

    public long TotalDropped { get; private set; }

    public Task SendAsync(string payload, CancellationToken ct)
    {
        ushort cmd = 0;
        try
        {
            if (!string.IsNullOrEmpty(payload) && payload.Length >= 16 &&
                MirPacketDecoder.TryDecode(payload, out var pkt))
                cmd = pkt.Header.Ident;
        }
        catch { /* 解不出就算了，命令码留 0 */ }

        _droppedByCmd.AddOrUpdate(cmd, 1, (_, old) => old + 1);
        TotalDropped++;

        if (TotalDropped <= 20 || TotalDropped % 100 == 0)
            Log?.Invoke($"[bridge] 丢弃未接管的动作 cmd={cmd}（累计 {TotalDropped} 次）" +
                        " —— 该 Send* 方法尚未接入 Driver，请按 CorePatch 文档补齐");

        // 关键：不写任何字节到真客户端的连接上
        return Task.CompletedTask;
    }

    /// <summary>把未接管命令汇总成一句人话，用于启动自检提示。</summary>
    public string DescribeDropped()
    {
        if (_droppedByCmd.IsEmpty) return "所有动作都已被 Driver 接管。";
        var parts = _droppedByCmd.OrderByDescending(kv => kv.Value)
                                 .Take(10)
                                 .Select(kv => $"cmd={kv.Key}×{kv.Value}");
        return "以下命令被丢弃（未接入 Driver）：" + string.Join("、", parts);
    }
}
