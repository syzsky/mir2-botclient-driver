using BotClient.Net;
using BotClient.Protocol;

namespace BotClient.Session;

/// <summary>
/// 自动发言管理器 — 定时向游戏发送聊天消息(CM_SAY)。
/// </summary>
public sealed class AutoChatManager
{
    private readonly BotSession _session;
    private CancellationTokenSource? _cts1;
    private CancellationTokenSource? _cts2;

    public AutoChatManager(BotSession session) => _session = session;

    /// <summary>启动第 i 组自动发言(i=0 或 1)</summary>
    public void Start(int index, string text, int intervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(text) || intervalSeconds < 10) return;

        Stop(index);

        var cts = new CancellationTokenSource();
        if (index == 0) _cts1 = cts; else _cts2 = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await SendChatAsync(text, cts.Token);
                    await Task.Delay(intervalSeconds * 1000, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }
        }, cts.Token);
    }

    public void Stop(int index)
    {
        var old = index == 0 ? _cts1 : _cts2;
        if (old != null)
        {
            old.Cancel();
            old.Dispose();
            if (index == 0) _cts1 = null; else _cts2 = null;
        }
    }

    public void StopAll()
    {
        Stop(0);
        Stop(1);
    }

    private async Task SendChatAsync(string text, CancellationToken ct)
    {
        // CM_SAY (3030): 发送聊天消息。格式: 头 + EdCode(body)
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_SAY, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString(text);
        await _session.SendPayloadAsync(payload, ct);
    }

    public void Dispose()
    {
        StopAll();
    }
}
