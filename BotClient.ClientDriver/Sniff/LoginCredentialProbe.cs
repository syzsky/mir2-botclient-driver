using System.Text;
using BotClient.Net;
using BotClient.Protocol;

namespace BotClient.ClientDriver.Sniff;

/// <summary>从客户端自己的登录流量里解出的凭据（只存内存，不落盘）。</summary>
public sealed record CapturedCredential(string Account, string Password, string MachineId, string FlowKey, DateTime At)
{
    /// <summary>日志用的脱敏展示：只留首尾字符与长度。</summary>
    public string Describe()
        => $"账号={Account} 密码={Mask(Password)} 机器码={Mask(MachineId)}";

    private static string Mask(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        if (s.Length <= 2) return new string('*', s.Length);
        return s[0] + new string('*', Math.Max(1, s.Length - 2)) + s[^1] + $"({s.Length}位)";
    }
}

/// <summary>
/// 登录凭据嗅探：**复用真机客户端自己的登录过程**，把账号密码从上行密文里解出来，
/// 而不是让用户手填、也不是让 Bot 再登录一次。
///
/// 原理（全部现成，未新增任何加密实现）：
///   ① 下行：LoginGate 连接建立后服务端推送 22 字符裸密钥包 → <see cref="LoginGateCrypto.DecodeKeyPacket"/>
///      解出本次会话的 ProtocolPassword；
///   ② 上行：客户端发出的 CM_IDPASSWORD 包 = 22 字符头 + 加密体，
///      解包链与 BotLoginFlow 发出去的完全对称：
///      线上体 → 6bit 解码 → DES 解密(IntToStr(pw)) → 6bit 解码 → GBK 明文 "account/password/machineid"。
///
/// 抓取窗口：**客户端发起登录的那一刻**。若宿主在客户端已登录之后才启动，拿不到本次登录包，
/// 会在客户端下次登录/重登（含换角色、掉线重连）时自然抓到。
/// </summary>
public sealed class LoginCredentialProbe
{
    private sealed class FlowState
    {
        public MirFrameCodec? DownCodec;
        public uint ProtocolPassword;
        public bool HavePassword;
        public bool CredentialDelivered;
        public readonly List<byte> UpAcc = new(4096);
    }

    private readonly Dictionary<string, FlowState> _flows = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>抓到一组账号密码（一次登录只触发一次）。</summary>
    public event Action<CapturedCredential>? Captured;

    public event Action<string>? Log;

    /// <summary>最近一次抓到的凭据（供宿主读取/自动重登使用）；未抓到为 null。</summary>
    public CapturedCredential? Latest { get; private set; }

    public void Feed(string flowKey, MirGateMode gate, FlowDirection dir, byte[] data)
    {
        if (data.Length == 0) return;
        if (gate == MirGateMode.RunGate) return;   // 进游戏后的流量不含登录凭据

        FlowState st;
        lock (_lock)
        {
            if (!_flows.TryGetValue(flowKey, out var existing))
            {
                existing = new FlowState();
                _flows[flowKey] = existing;
            }
            st = existing;
        }

        try
        {
            if (dir == FlowDirection.Downstream) OnDown(flowKey, gate, st, data);
            else OnUp(flowKey, gate, st, data);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[cred] 解析 {flowKey} 异常: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 下行：拿 ProtocolPassword

    private void OnDown(string flowKey, MirGateMode gate, FlowState st, byte[] data)
    {
        if (gate != MirGateMode.LoginGate) return;   // 只有 LoginGate 有密钥包

        st.DownCodec ??= new MirFrameCodec(MirGateMode.LoginGate);
        foreach (var (frame, _) in st.DownCodec.Feed(data))
        {
            if (frame.Kind != MirFrameKind.KeyPacket) continue;
            string? keyText = frame.Text;
            if (string.IsNullOrEmpty(keyText)) continue;
            st.ProtocolPassword = LoginGateCrypto.DecodeKeyPacket(keyText);
            st.HavePassword = true;
            Log?.Invoke($"[cred] 已从 {flowKey} 解出登录会话密钥（可用以解开客户端上行登录包）");
        }
    }

    // ------------------------------------------------------------------ 上行：解 CM_IDPASSWORD

    private void OnUp(string flowKey, MirGateMode gate, FlowState st, byte[] data)
    {
        if (gate == MirGateMode.LoginGate)
        {
            if (st.CredentialDelivered) return;
            st.UpAcc.AddRange(data);
            DrainLoginFrames(flowKey, st);
            return;
        }

        if (gate == MirGateMode.SelGate)
        {
            // 选角段上行是明文，账号明文可见（无密码）：作为"只拿到账号"的兜底
            string text = Encoding.ASCII.GetString(data);
            foreach (string account in ExtractAccounts(text))
                Publish(account, string.Empty, string.Empty, flowKey, partial: true);
        }
    }

    private void DrainLoginFrames(string flowKey, FlowState st)
    {
        while (true)
        {
            int hash = st.UpAcc.IndexOf((byte)'#');
            if (hash < 0)
            {
                if (st.UpAcc.Count > 8192) st.UpAcc.Clear();
                return;
            }
            if (hash > 0) st.UpAcc.RemoveRange(0, hash);

            int bang = st.UpAcc.IndexOf((byte)'!');
            if (bang < 0)
            {
                if (st.UpAcc.Count > 65536) st.UpAcc.Clear();   // 异常流保护
                return;
            }

            byte[] raw = st.UpAcc.GetRange(0, bang + 1).ToArray();
            st.UpAcc.RemoveRange(0, bang + 1);
            if (raw.Length < 4) continue;

            string inner = Encoding.ASCII.GetString(raw, 1, raw.Length - 2);
            if (TryParseIdPassword(flowKey, st, inner)) return;
        }
    }

    /// <summary>
    /// 22 字符头 + 加密体；上行统一是 '#' + 1 位序号 + payload + '!'，序号位是数字字符，
    /// 所以先试偏移 1（带序号），再试偏移 0（不带），谁解出的 Ident 命中就用谁。
    /// </summary>
    private bool TryParseIdPassword(string flowKey, FlowState st, string inner)
    {
        int[] offsets = inner.Length > 0 && char.IsDigit(inner[0]) ? new[] { 1, 0 } : new[] { 0, 1 };

        foreach (int off in offsets)
        {
            if (inner.Length - off <= 22) continue;
            string head = inner.Substring(off, 22);
            if (!TryDecodeHeader(head, out var pkt)) continue;
            if (pkt.Ident != Grobal2.CM_IDPASSWORD) continue;

            if (!st.HavePassword)
            {
                Log?.Invoke($"[cred] 收到登录包但尚未拿到会话密钥（{flowKey}），本次跳过");
                return false;
            }

            string body = inner.Substring(off + 22);
            string? plain = TryDecryptBody(body, st.ProtocolPassword);
            if (plain == null) continue;   // 密钥/偏移不对，换个偏移再试

            string[] parts = plain.Split('/');
            if (parts.Length < 2) continue;

            Publish(parts[0], parts[1], parts.Length > 2 ? parts[2] : string.Empty, flowKey, partial: false);
            // 本次登录的凭据已拿到：停解该流，避免长时间挂机时上行缓冲区反复累积
            st.CredentialDelivered = true;
            st.UpAcc.Clear();
            return true;
        }

        return false;
    }

    private static bool TryDecodeHeader(string head, out CmdPack pkt)
    {
        pkt = default;
        try
        {
            var decoded = EdCode.DecodeMessage(head);
            if (decoded.Ident == 0 || decoded.Ident > 20000) return false;
            pkt = decoded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>线上密文体 → 明文（与 BotLoginFlow 的发包链完全对称的反向链）。</summary>
    private static string? TryDecryptBody(string bodyEncoded, uint pw)
    {
        try
        {
            byte[] cipher = EdCode.DecodeBytes(bodyEncoded);
            byte[] inner = UnitDes.DecryptDes(cipher, Convert.ToString(pw));
            string innerStr = Encoding.ASCII.GetString(inner);
            byte[] plain = EdCode.DecodeBytes(innerStr);
            return GbkEncoding.Instance.GetString(plain);
        }
        catch
        {
            return null;   // 密钥不对/包不完整：交给下一次
        }
    }

    private void Publish(string account, string password, string machineId, string flowKey, bool partial)
    {
        if (string.IsNullOrWhiteSpace(account)) return;
        string acc = account.Trim();

        var prev = Latest;
        if (prev != null)
        {
            // 已经有记录了：只有"同账号的完整凭据来补全密码"才允许覆盖
            if (partial) return;
            if (prev.Account == acc && !string.IsNullOrEmpty(prev.Password)) return;
        }

        var cred = new CapturedCredential(acc, password, machineId, flowKey, DateTime.Now);
        Latest = cred;
        Log?.Invoke($"[cred] 已从客户端登录流量解出凭据（{cred.Describe()}）");
        Captured?.Invoke(cred);
    }

    private static IEnumerable<string> ExtractAccounts(string text)
    {
        // SelGate 明文形如 "...账号/证书..."，粗略提取：取长度合理且不含控制字符的段
        foreach (string seg in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string s = new string(seg.Where(c => !char.IsControl(c)).ToArray());
            if (s.Length is >= 2 and <= 32 && s.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.'))
                yield return s;
        }
    }
}
