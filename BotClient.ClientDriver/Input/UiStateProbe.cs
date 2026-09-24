using BotClient.ClientDriver;
using BotClient.Protocol;

namespace BotClient.ClientDriver.Input;

/// <summary>客户端当前的 UI 场景态。</summary>
public enum UiState
{
    /// <summary>自由态：点地图走位、点目标攻击。</summary>
    Free,
    /// <summary>NPC 对话/商店态：此时点地图是无效的，必须点菜单行。</summary>
    NpcDialog,
    /// <summary>换图/黑屏加载：一切点击都必须禁止（点下去会落到还没渲染完的界面上）。</summary>
    MapLoading,
    /// <summary>还不知道（刚启动、抓不到包）。此状态下不允许任何点击。</summary>
    Unknown,
}

/// <summary>
/// UI 场景态机 —— C 方案的"眼睛"。
///
/// 为什么必须有它：Bot 看不见画面，但它知道序列化层的全部事实。
/// 同一个鼠标左键，在自由态是"走向那一格"，在对话态是"选择第 3 个菜单项"，
/// 在加载态是"什么都不该发生"。**点错状态 = 触发一个你完全没打算执行的操作**，
/// 所以每次点击前都要先问它"现在是不是自由态"。
///
/// 状态来源全部是嗅探到的真实包，没有任何猜测：
///   自由 → 对话：服务端下发 NPC 对话/商店窗口（SmNpcDialog）
///   对话 → 自由：对话框是**客户端本地关闭**的，嗅探看不到关闭动作，
///               因此只能靠①收到换图包 ②对话动作后超时 两种方式降级。
///               超时兜底是必要的妥协，宁可多等一会儿，也不要误点。
///   任意 → 加载：收到换图包（SmMapChange）
///   加载 → 自由：收到地图描述包 / 坐标包
/// </summary>
public sealed class UiStateProbe
{
    private readonly ICmdCatalog _cmds;
    private readonly object _lock = new();

    private DateTime _mapLoadDeadline = DateTime.MinValue;
    private DateTime _dialogDeadline = DateTime.MinValue;

    public UiStateProbe(ICmdCatalog cmds) => _cmds = cmds;

    /// <summary>换图后禁止点击的时长（毫秒）。默认取配置 Behavior.MapLoadGuardMs。</summary>
    public int MapLoadGuardMs { get; set; } = 1500;

    /// <summary>对话态静默多久后认为对话框已被关闭。传奇脚本菜单常有多级，留宽一些。</summary>
    public int DialogIdleTimeoutMs { get; set; } = 9000;

    public UiState State { get; private set; } = UiState.Unknown;

    /// <summary>当前对话框里的菜单文本（如果能从包里解出来的话），用于按文本匹配选项。</summary>
    public List<string> DialogLines { get; } = new();

    /// <summary>
    /// 当前对话框菜单项的结构化形式（显示文本 → 回传命令），与 <see cref="DialogLines"/> 同序等长。
    /// 由 <see cref="FeedNpcDialog"/> 从 Core 的 NPC 正文里解出；行号就是这里的下标。
    /// </summary>
    public List<NpcOption> DialogOptions { get; } = new();

    /// <summary>下发当前对话框的 NPC 标识（SM_MERCHANTSAY 的 Recog），0 表示未知。</summary>
    public long DialogOwner { get; private set; }

    /// <summary>
    /// 服务端最近一条提示正文（SM_MENU_OK）。传送/进图失败时这里就是失败原因，
    /// 例如"你的等级不够"、"需要xx物品"。由宿主转发 SystemMessage 填充。
    /// </summary>
    public string LastSystemMessage { get; private set; } = string.Empty;

    /// <summary>上面那条提示的到达时间，用于判定"这条提示是不是本次点选产生的"。</summary>
    public DateTime LastSystemMessageAt { get; private set; } = DateTime.MinValue;

    public event Action<UiState, UiState>? StateChanged;
    public event Action<string>? Log;

    public bool AllowsClick
    {
        get { lock (_lock) return State == UiState.Free; }
    }

    // ---------------------------------------------------------------- 输入：服务端下行

    public void OnServerCommand(ushort cmd, CmdPack pack)
    {
        lock (_lock)
        {
            if (Hit(cmd, _cmds.SmMapChange))
            {
                Transition(UiState.MapLoading, "收到换图包");
                _mapLoadDeadline = DateTime.UtcNow.AddMilliseconds(MapLoadGuardMs);
                DialogLines.Clear();
                return;
            }

            if (Hit(cmd, _cmds.SmMapDescription))
            {
                // 新地图描述到了 —— 加载基本完成
                if (State == UiState.MapLoading)
                {
                    Transition(UiState.Free, "新地图描述已到达");
                    _mapLoadDeadline = DateTime.MinValue;
                }
                return;
            }

            if (Hit(cmd, _cmds.SmPositionMove))
            {
                // 收到自己/他人的坐标广播：说明世界已在跑
                if (State == UiState.MapLoading && DateTime.UtcNow >= _mapLoadDeadline)
                    Transition(UiState.Free, "坐标广播恢复");
                return;
            }

            if (Hit(cmd, _cmds.SmNpcDialog))
            {
                if (State != UiState.NpcDialog)
                    Transition(UiState.NpcDialog, "服务端下发了 NPC 对话窗口");
                _dialogDeadline = DateTime.UtcNow.AddMilliseconds(DialogIdleTimeoutMs);
                return;
            }

            if (Hit(cmd, _cmds.SmActionRet))
            {
                // 动作被拒绝（比如走了个不可达点）：不代表 UI 态变化，仅刷新对话态存活
                if (State == UiState.NpcDialog)
                    _dialogDeadline = DateTime.UtcNow.AddMilliseconds(DialogIdleTimeoutMs);
            }
        }
    }

    // ---------------------------------------------------------------- 输入：客户端上行

    /// <summary>
    /// 客户端发出的上行命令。用于两件事：
    ///   ① 确认我们点的东西客户端真的接受了；
    ///   ② 在对话态里点了菜单之后，延长"等待服务端下一步"的窗口。
    /// </summary>
    public void OnClientCommand(ushort cmd)
    {
        lock (_lock)
        {
            if (Hit(cmd, _cmds.CmMerchantSelect) || Hit(cmd, _cmds.CmNpcInteract))
            {
                // 已经选了：服务端马上会回下一个窗口或换图，先把窗口往后推，
                // 避免在服务端答复到达前被超时降级成自由态而误点地图
                if (State == UiState.NpcDialog)
                    _dialogDeadline = DateTime.UtcNow.AddMilliseconds(DialogIdleTimeoutMs);
            }
        }
    }

    // ---------------------------------------------------------------- 周期推进

    /// <summary>由主循环每 200ms 左右调一次，处理超时降级。</summary>
    public void Tick()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            if (State == UiState.MapLoading && _mapLoadDeadline != DateTime.MinValue && now >= _mapLoadDeadline)
            {
                // 加载保护期已过但一直没收到新地图描述（有些客户端换图不重发描述）
                Transition(UiState.Free, "加载保护期结束");
                _mapLoadDeadline = DateTime.MinValue;
            }

            if (State == UiState.NpcDialog && now >= _dialogDeadline)
            {
                Transition(UiState.Free, "对话窗口静默超时（应已关闭）");
                DialogLines.Clear();
            }
        }
    }

    /// <summary>
    /// 已知对话框内容时喂进来，用于按**文本**匹配选项而不是按行号 ——
    /// 行号会随着首页/翻页变化，文本不会。
    /// </summary>
    public void FeedDialogText(IEnumerable<string> lines)
    {
        lock (_lock)
        {
            DialogLines.Clear();
            DialogOptions.Clear();
            DialogLines.AddRange(lines.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    /// <summary>
    /// 把 Core 侧解出的 NPC 对话正文喂进来 —— 这是"菜单文本匹配"真正的数据来源。
    ///
    /// 调用点只有一个：宿主把 <c>BotRuntime.NpcMessage</c>（SM_MERCHANTSAY）与
    /// <c>BotRuntime.SystemMessage</c>（SM_MENU_OK，提示/确认框）转发到
    /// <c>ClientDriverHost.FeedNpcDialog</c>。序列化正文里
    /// <c>"&lt;买/@buy&gt; 武器"</c> 这类选项由 Core 的 <see cref="NpcDialogOptions"/> 解析，
    /// 解析结果与对话框**行号同序**，因此可以直接用行号点第 N 行。
    ///
    /// 解析不出选项时**不清空**已有内容：服务端下发纯正文（例如"你身上没有这个东西"）
    /// 不代表菜单消失了，清空会让下一步选择退化成按行号盲点。
    /// </summary>
    public void FeedNpcDialog(long merchantId, string? rawText)
    {
        lock (_lock)
        {
            var options = NpcDialogOptions.Parse(rawText);
            if (options.Count == 0)
            {
                Log?.Invoke("[ui] NPC 正文里没解出菜单项，保留上一页菜单内容");
                NoteDialogActivity();
                return;
            }

            DialogOwner = merchantId;
            DialogOptions.Clear();
            DialogOptions.AddRange(options);
            DialogLines.Clear();
            DialogLines.AddRange(options.Select(o => o.Display));

            if (State != UiState.NpcDialog)
                Transition(UiState.NpcDialog, $"收到 NPC({merchantId}) 菜单 {options.Count} 项");
            _dialogDeadline = DateTime.UtcNow.AddMilliseconds(DialogIdleTimeoutMs);
            Log?.Invoke($"[ui] NPC 菜单已更新（{options.Count} 项）：{string.Join(" | ", DialogLines)}");
        }
    }

    /// <summary>
    /// 收到提示/确认类下行（SM_MENU_OK）。记录正文作为"本次点选的结果说明"，
    /// 已处于对话态时顺带续期；**不改变状态**，也不当作菜单解析。
    /// </summary>
    public void NoteDialogActivity(string? text = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                LastSystemMessage = text!;
                LastSystemMessageAt = DateTime.UtcNow;
            }
            if (State == UiState.NpcDialog)
                _dialogDeadline = DateTime.UtcNow.AddMilliseconds(DialogIdleTimeoutMs);
        }
    }

    /// <summary>
    /// 清掉上一条提示并返回"现在的时刻"。传送探测每次点选前调用一次：
    /// 之后凡 <see cref="LastSystemMessageAt"/> 晚于该时刻的提示，就是**本次点选**的结果。
    /// 不清掉历史提示的话，会把上一轮的回绝误判成本轮的结果。
    /// </summary>
    public DateTime ClearSystemMessage()
    {
        lock (_lock)
        {
            LastSystemMessage = string.Empty;
            LastSystemMessageAt = DateTime.MinValue;
            return DateTime.UtcNow;
        }
    }

    /// <summary>外部强制设态（人工干预 / 抓包中断恢复）。</summary>
    public void ForceState(UiState state, string reason)
    {
        lock (_lock) Transition(state, reason);
    }

    private void Transition(UiState next, string reason)
    {
        if (State == next) return;
        var old = State;
        State = next;
        Log?.Invoke($"[ui] {old} → {next}（{reason}）");
        StateChanged?.Invoke(old, next);
    }

    /// <summary>命令码比较：0 表示该项未解析到，**绝不匹配**。</summary>
    private static bool Hit(ushort actual, ushort expected) => expected != 0 && actual == expected;
}
