using System.Text;
using BotClient.Net;
using BotClient.Protocol;

namespace BotClient.Session;

/// <summary>
/// 登录/选角协议发包助手(脱机 Bot 自包含,源自 MirClientSession 关键路径)。
/// </summary>
public sealed class BotLoginFlow
{
    private readonly BotSession _session;
    public event Action<string>? Log;

    /// <summary>UI 进度提示(如"正在连接服务器..."),由 LoginForm 订阅。</summary>
    public event Action<string>? Progress;

    private void SetProgress(string text)
    {
        BotLog.Info($"UI进度: {text}");
        Progress?.Invoke(text);
    }

    public BotLoginFlow(BotSession session) => _session = session;

    /// <summary>离线 Bot 的机器标识 (gate 侧仅用于 MAC 黑名单/多开限制,本机调试无黑名单)。
    /// 取真实客户端在 RunGate 日志中的机器码, 避免服务端按 MAC 绑定时拒绝。</summary>
    public const string MachineId = "63D0722CD7073F602C20B43743C7CB66";

    public async Task<MirServerPacket> ConnectAndLoginAsync(string host, int port, string account, string password, CancellationToken ct)
    {
        BotLog.Info($"登录开始: {account}@{host}:{port}");
        Log?.Invoke($"[login] connecting {host}:{port}");
        SetProgress("正在连接登录服务器...");
        await _session.ConnectAsync(host, port, MirGateMode.LoginGate, ct).ConfigureAwait(false);
        _session.SetStage(MirSessionStage.LoginGate);
        _session.SetAccount(account);

        // 连接后 LoginGate 立即推送 22 字符密钥包, 解出 ProtocolPassword 供上行加密
        SetProgress("正在接收协议密钥...");
        uint pw;
        try
        {
            pw = await _session.WaitForProtocolPasswordAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("未收到 LoginGate 密钥包(22字符), 请确认连接到的是 gxx LoginGate 端口。");
        }

        // CM_IDPASSWORD: 加密体 "account/password/machineid", Recog 低位=混合哈希
        // (gxx LoginGate 白名单不含 CM_PROTOCOL, 真实客户端也不发, 故此处不发送)
        SetProgress("正在验证账号密码...");
        string payload = LoginGateCrypto.BuildEncryptedPayload(
            Grobal2.CM_IDPASSWORD, $"{account}/{password}/{MachineId}", pw);

        MirServerPacket resp;
        for (int attempt = 0; ; attempt++)
        {
            BotLog.Packet("send", Grobal2.CM_IDPASSWORD, $"account={account} 第{attempt + 1}次");
            Log?.Invoke($"[login] -> CM_IDPASSWORD{(attempt > 0 ? $" (重试 {attempt + 1})" : "")}");
            await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);

            // 等服务端响应:SM_PASSOK_SELECTSERVER 成功,SM_PASSWD_FAIL/SM_ID_NOTFOUND 失败
            BotLog.Info("等待登录响应...");
            resp = await _session.WaitForPacketAsync(
                p => p.Header.Ident is Grobal2.SM_PASSOK_SELECTSERVER or Grobal2.SM_PASSWD_FAIL or Grobal2.SM_ID_NOTFOUND,
                TimeSpan.FromSeconds(15),
                ct).ConfigureAwait(false);
            BotLog.Packet("recv", resp.Header.Ident, $"登录响应 Recog={resp.Header.Recog}");

            // Recog=-3 不是密码错误:LoginSrv 发现该账号还挂着上一次会话(选服后未正常退出游戏时
            // 会话不会释放),此时它已把旧会话标记为 boKicked(2 秒后清理)并回 -3。
            // 见 LoginSrv/LMain.pas:3180-3184 + SessionKick/SessionClearKick。等旧会话清掉再发一次即可。
            if (resp.Header.Ident == Grobal2.SM_PASSWD_FAIL && resp.Header.Recog == -3 && attempt == 0)
            {
                SetProgress("检测到上次登录会话未释放,正在释放后重试...");
                Log?.Invoke("[login] SM_PASSWD_FAIL(-3) 上次会话未过期,服务端已踢旧会话,3 秒后重试");
                await Task.Delay(3000, ct).ConfigureAwait(false);
                continue;
            }
            break;
        }

        if (resp.Header.Ident == Grobal2.SM_ID_NOTFOUND)
            throw new InvalidOperationException("账号不存在(请先注册账号)。");

        if (resp.Header.Ident == Grobal2.SM_PASSWD_FAIL)
            throw new InvalidOperationException(DescribeLoginFail(resp.Header.Recog));

        _session.SetStage(MirSessionStage.SelectServer);
        Log?.Invoke("[login] SM_PASSOK_SELECTSERVER received");
        return resp;
    }

    /// <summary>解析 SM_PASSOK_SELECTSERVER 里的服列表(body 形如 "热血传奇/1/")。</summary>
    public static List<string> ParseServerList(MirServerPacket passOk)
    {
        string decoded = EdCode.DecodeString(passOk.BodyEncoded);
        var result = new List<string>();
        foreach (var part in decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrEmpty(part))
                result.Add(part);
        }
        return result;
    }

    public async Task SelectServerAsync(MirServerPacket passOk, string serverName, CancellationToken ct)
    {
        // CM_SELECTSERVER: 加密体 = 服务器名
        SetProgress($"正在选择服务器[{serverName}]...");
        uint pw = _session.ProtocolPassword;
        string payload = LoginGateCrypto.BuildEncryptedPayload(Grobal2.CM_SELECTSERVER, serverName, pw);
        Log?.Invoke($"[login] -> CM_SELECTSERVER ({serverName})");
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);

        // 等 SM_SELECTSERVER_OK, body 四段 "ip/port/sessionID/account" (LoginSrv LMain.pas:3804)
        var resp = await _session.WaitForPacketAsync(
            p => p.Header.Ident is Grobal2.SM_SELECTSERVER_OK or Grobal2.SM_STARTFAIL,
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        if (resp.Header.Ident == Grobal2.SM_STARTFAIL)
            throw new InvalidOperationException("选服被拒(SM_STARTFAIL): 该服务器会话已满。");

        string decoded = EdCode.DecodeString(resp.BodyEncoded);
        var parts = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            throw new InvalidOperationException($"SM_SELECTSERVER_OK body 格式异常:{decoded}");

        string selHost = parts[0];
        int selPort = int.Parse(parts[1]);
        int cert = int.Parse(parts[2]);

        _session.SetSelGate(selHost, selPort);
        _session.SetCertification(cert);
        Log?.Invoke($"[login] SM_SELECTSERVER_OK selGate={selHost}:{selPort} cert={cert}");

        // 切换到 SelGate:断开 LoginGate,连到 SelGate,发 CM_QUERYCHR
        SetProgress($"正在连接选角网关[{selHost}:{selPort}]...");
        await _session.DisconnectAsync().ConfigureAwait(false);
        await _session.ConnectAsync(selHost, selPort, MirGateMode.SelGate, ct).ConfigureAwait(false);
        _session.SetStage(MirSessionStage.SelGate);

        SetProgress("正在查询角色列表...");
        await SendQueryChrAsync(ct).ConfigureAwait(false);
    }

    private async Task SendQueryChrAsync(CancellationToken ct)
    {
        var queryMsg = CmdPack.MakeDefaultMsg(Grobal2.CM_QUERYCHR, 0, 0, 0, 0);
        string queryPayload = EdCode.EncodeMessage(queryMsg) + EdCode.EncodeString($"{_session.Account ?? string.Empty}/{_session.Certification}");
        Log?.Invoke("[selchr] -> CM_QUERYCHR");
        await _session.SendPayloadAsync(queryPayload, ct).ConfigureAwait(false);
    }

    /// <summary>重新查询角色列表(建角后刷新)。</summary>
    public async Task<List<string>> RequeryCharactersAsync(CancellationToken ct)
    {
        if (_session.Stage != MirSessionStage.SelGate)
            throw new InvalidOperationException("当前不在选角网关连接上,无法查询角色。");
        await SendQueryChrAsync(ct).ConfigureAwait(false);
        return await WaitForCharacterListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>等 SM_QUERYCHR 返回,body 形如 "*名字/0/1/等级/性别/职业/*名字2/..."。
    /// 优先取以 * 开头的字段(标准格式), 如果没有则用非数字字段判断(兼容旧版)。</summary>
    public async Task<List<string>> WaitForCharacterListAsync(CancellationToken ct)
        => await WaitCharacterListStaticAsync(_session, ct).ConfigureAwait(false);

    /// <summary>静态版:供小退后不经过完整登录流程时复用。</summary>
    public static async Task<List<string>> WaitCharacterListStaticAsync(BotSession session, CancellationToken ct)
    {
        var resp = await session.WaitForPacketAsync(
            p => p.Header.Ident is Grobal2.SM_QUERYCHR or Grobal2.SM_QUERYCHR_FAIL,
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        if (resp.Header.Ident == Grobal2.SM_QUERYCHR_FAIL)
            return new List<string>();

        string decoded = EdCode.DecodeString(resp.BodyEncoded);
        var parts = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chars = new List<string>();

        // 方案1: 取以 * 开头的字段作为角色名(标准格式)
        foreach (var part in parts)
        {
            if (part.Length > 1 && part[0] == '*')
                chars.Add(part[1..].Trim());
        }

        // 方案2: 如果没有带*的, 过滤非数字字段(兼容旧版格式: 角色名/0/1/等级/性别/职业/...)
        if (chars.Count == 0)
        {
            foreach (var part in parts)
            {
                // 角色名含中文或字母, 不是纯数字
                if (part.Length > 0 && !part.All(char.IsDigit) && !string.IsNullOrEmpty(part))
                    chars.Add(part.Trim());
            }
        }

        return chars;
    }

    /// <summary>选角发 CM_SELCHR,然后等 SM_STARTPLAY 拿到 RunGate host/port。</summary>
    public async Task SelectCharacterAsync(string characterName, CancellationToken ct)
    {
        SetProgress($"正在选择角色[{characterName}]...");
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_SELCHR, 0, 0, 0, 0);
        string payload = EdCode.EncodeMessage(msg) + EdCode.EncodeString($"{_session.Account}/{characterName}");
        Log?.Invoke($"[selchr] -> CM_SELCHR ({characterName})");
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);

        var resp = await _session.WaitForPacketAsync(
            p => p.Header.Ident is Grobal2.SM_STARTPLAY or Grobal2.SM_STARTFAIL,
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        if (resp.Header.Ident == Grobal2.SM_STARTFAIL)
            throw new InvalidOperationException($"选角被服务端拒绝(SM_STARTFAIL Recog={resp.Header.Recog})。可能游戏引擎(Mir200)未就绪。");

        string decoded = EdCode.DecodeString(resp.BodyEncoded);
        var parts = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException($"SM_STARTPLAY body 格式异常:{decoded}");

        string runHost = parts[0];
        int runPort = int.Parse(parts[1]);
        _session.SetRunGate(runHost, runPort);
        Log?.Invoke($"[selchr] SM_STARTPLAY runGate={runHost}:{runPort}");

        // 切换到 RunGate:断开 SelGate,连到 RunGate,发 RunGateLogin
        SetProgress($"正在连接游戏网关[{runHost}:{runPort}]...");
        BotLog.Info("[rungate] 断开 SelGate...");
        await _session.DisconnectAsync().ConfigureAwait(false);
        BotLog.Info($"[rungate] 连接 RunGate {runHost}:{runPort}...");
        await _session.ConnectAsync(runHost, runPort, MirGateMode.RunGate, ct).ConfigureAwait(false);
        BotLog.Info("[rungate] RunGate 已连接,设置阶段");
        _session.SetStage(MirSessionStage.RunGate);

        // RunGate 首包 = 登录证书行 (不是 TDefaultMessage!)：'#' + 序号 + EncodeString('**' + 15 段) + '!'。
        // 字段顺序由服务端解析器决定: RunGate/MirClientContext.pas:2086-2100 (13段) +
        // M2Engine/RunSock.pas:1188-1204 (15段, GetCertification)。M2 侧要求
        // sAccount<>'' && sChrName<>'' && nSessionID>=2, 且 nSessionID 必须是 LoginSrv
        // 在 SS_OPENSESSION 里登记过的会话号(即 SM_SELECTSERVER_OK 的第 3 段 cert)。
        // 注意: HUtil32.GetValidStr3 会整段跳过空字段(连续 '//')，空字段会让后面所有字段
        // 前移错位 —— 实测 RunGate 日志把角色名解析成了会话号。因此每段必须非空, 用 '-' 占位。
        SetProgress("正在登录游戏世界...");
        string loginMsg = "**" + string.Join('/',
            "-",                             // 0 sClientPassWord (CheckClientPassWord 未启用)
            _session.Account!,               // 1 sAccount
            characterName,                   // 2 sChrName
            _session.Certification,          // 3 nSessionID
            Grobal2.CLIENT_VERSION_NUMBER,   // 4 sClientVersion
            "-",                             // 5 sKey        (EncryString_LF(g_PKey))
            "-",                             // 6 sCheckKey
            Grobal2.RUNLOGINCODE,            // 7 sRunLoginCode (1=断线重连)
            MachineId,                       // 8 sMachineID
            MachineId,                       // 9 sUserMachineID
            1024,                            // 10 客户端宽
            768,                             // 11 客户端高
            "-",                             // 12 sGameLoginConfigUrlMD5
            "-",                             // 13 sClientBuildVer
            "-");                            // 14 sPromotionFlag
        string loginPayload = EdCode.EncodeString(loginMsg);
        Log?.Invoke("[rungate] -> RunGateLogin");
        BotLog.Info($"[rungate] RunGateLogin 原文 = {loginMsg}");
        BotLog.Info($"[rungate] cert={_session.Certification} version={Grobal2.CLIENT_VERSION_NUMBER} payloadLen={loginPayload.Length}");
        await _session.SendPayloadAsync(loginPayload, ct).ConfigureAwait(false);
        BotLog.Info("[rungate] RunGateLogin 已发送");
        _session.SetStage(MirSessionStage.Playing);

        // 确认 RunGate 回了任意游戏内响应(SM_LOGON/SM_SENDNOTICE 等),但首包不消费——
        // 留给 BotRuntime 处理(公告要发 CM_LOGINNOTICEOK 确认,否则服务端卡住不发后续包)。
        try
        {
            bool got = await _session.PeekPacketAsync(
                p => p.Header.Ident is Grobal2.SM_LOGON
                     or Grobal2.SM_OUTOFCONNECTION
                     or Grobal2.SM_STARTFAIL
                     or Grobal2.SM_SENDNOTICE
                     or Grobal2.SM_NEWMAP
                     or Grobal2.SM_ABILITY,
                TimeSpan.FromSeconds(10),
                ct).ConfigureAwait(false);

            if (!got)
            {
                BotLog.Warn("[rungate] 等待首包超时(10s),RunGate 未响应登录。继续进入主界面以便观察。");
            }
            else
            {
                BotLog.Info("[rungate] Peek 到首包,RunGate 登录成功(首包留给 BotRuntime 处理)");
            }
        }
        catch (OperationCanceledException)
        {
            BotLog.Warn("[rungate] 等待首包超时(10s),RunGate 未响应登录。继续进入主界面以便观察。");
        }
    }

    /// <summary>SM_PASSWD_FAIL 的 Recog 含义(LoginSrv/LMain.pas AccountLogin:3128-3184)。</summary>
    private static string DescribeLoginFail(long code) => code switch
    {
        -1 => "账号或密码错误。",
        -2 => "密码连续错误 5 次,账号已被临时锁定(60 秒后自动解除)。",
        -3 => "上一次登录会话未释放(该账号在游戏内未正常退出),请稍后重试。",
        _ => $"登录被服务端拒绝(Recog={code})。",
    };

    /// <summary>在建角界面建新角色(CM_NEWCHR,走 SelGate 明文链)。
    /// 字段顺序由 DBServer/SelectClient.pas NewChr 决定: account/名字/发型/职业/性别。
    /// 必须在同一 SelGate 连接上、且已成功 CM_QUERYCHR 之后调用(DBServer 用 QUERYCHR
    /// 建立的会话信息校验 CheckSession),并间隔 1 秒以上(NewChr 有 dwChrTick>1000 限速)。</summary>
    public async Task CreateCharacterAsync(string characterName, int job = 0, int sex = 0, CancellationToken ct = default)
    {
        if (_session.Stage != MirSessionStage.SelGate)
            throw new InvalidOperationException("建角必须在选角网关(SelGate)连接上进行,请先完成登录与选服。");
        if (string.IsNullOrWhiteSpace(characterName))
            throw new ArgumentException("角色名不能为空。", nameof(characterName));

        string hair = sex == 0 ? "2" : "3";   // 与原版客户端 IntroScn.SelChrNewOk 一致
        string body = EdCode.EncodeString($"{_session.Account}/{characterName.Trim()}/{hair}/{job}/{sex}");
        var msg = CmdPack.MakeDefaultMsg(Grobal2.CM_NEWCHR, 0, 0, 0, 0);

        await Task.Delay(1200, ct).ConfigureAwait(false);   // 跨过 DBServer 的 1 秒建角限速
        SetProgress($"正在创建角色[{characterName}]...");
        Log?.Invoke($"[newchr] -> CM_NEWCHR ({characterName} 职业={job} 性别={sex} 发型={hair})");
        await _session.SendPayloadAsync(EdCode.EncodeMessage(msg) + body, ct).ConfigureAwait(false);

        var resp = await _session.WaitForPacketAsync(
            p => p.Header.Ident is Grobal2.SM_NEWCHR_SUCCESS or Grobal2.SM_NEWCHR_FAIL,
            TimeSpan.FromSeconds(10),
            ct).ConfigureAwait(false);

        if (resp.Header.Ident == Grobal2.SM_NEWCHR_FAIL)
            throw new InvalidOperationException($"建角失败(原因码={resp.Header.Recog}):{DescribeNewChrFail(resp.Header.Recog)}");

        Log?.Invoke("[newchr] SM_NEWCHR_SUCCESS");
        SetProgress("角色创建成功");
    }

    private static string DescribeNewChrFail(long code) => code switch
    {
        0 => "角色名不合法(长度需 2 个汉字/4 字符以上且只含中英文)",
        2 => "该名字已被占用或在禁用名单中",
        3 => "该账号角色数量已达上限",
        5 => "服务端当前未开放创建角色",
        6 => "服务端禁止使用含数字的名字",
        7 => "服务端禁止使用纯字母名字",
        8 => "名字含敏感词",
        _ => $"未知错误码 {code}",
    };

    /// <summary>注册新账号(CM_ADDNEWUSER, 走 LoginGate 加密链)。注册成功后账号可直接登录。</summary>
    public async Task CreateAccountAsync(string host, int port, string account, string password, CancellationToken ct)
    {
        Log?.Invoke($"[register] connecting {host}:{port}");
        await _session.ConnectAsync(host, port, MirGateMode.LoginGate, ct).ConfigureAwait(false);
        _session.SetStage(MirSessionStage.LoginGate);

        uint pw = await _session.WaitForProtocolPasswordAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        // LoginSrv 对 CM_ADDNEWUSER 有 3 秒限速: (GetTickCount - UserInfo.dwClientTick) > 3000 才建号,
        // 否则只往窗口打 "[限速操作] 创建账号" 且不回包(表现为客户端永久等待)。见 LMain.pas:1829-1847。
        await Task.Delay(3500, ct).ConfigureAwait(false);

        var entry = new MirAccountFullEntry(
            Account: account.Trim(),
            Password: password,
            UserName: account.Trim(),
            Quiz1: "1",
            Answer1: "1",
            Quiz2: "2",
            Answer2: "2",
            BirthDay: "1972/01/01",
            SSNo: "720101-1467202",
            Phone: string.Empty,
            EMail: string.Empty,
            MobilePhone: string.Empty);

        byte[] buffer = BuildUserFullEntryBuffer(entry);
        string payload = LoginGateCrypto.BuildEncryptedPayloadRaw(Grobal2.CM_ADDNEWUSER, buffer, pw);
        Log?.Invoke("[register] -> CM_ADDNEWUSER");
        await _session.SendPayloadAsync(payload, ct).ConfigureAwait(false);

        var resp = await _session.WaitForPacketAsync(
            p => p.Header.Ident is Grobal2.SM_NEWID_SUCCESS or Grobal2.SM_NEWID_FAIL,
            TimeSpan.FromSeconds(15),
            ct).ConfigureAwait(false);

        if (resp.Header.Ident == Grobal2.SM_NEWID_FAIL)
            throw new InvalidOperationException($"注册失败(Code={resp.Header.Recog})。");

        Log?.Invoke("[register] 注册成功");
    }

    /// <summary>逐字对应 gxx Grobal2.pas TUserEntry(147B)+TUserEntryAdd(112B): 15 个定长 ShortString。</summary>
    private static byte[] BuildUserFullEntryBuffer(MirAccountFullEntry entry)
    {
        using var ms = new MemoryStream(259);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.Account, 10), 10);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.Password, 10), 10);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.UserName, 20), 20);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.SSNo, 14), 14);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.Phone, 14), 14);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.Quiz1, 20), 20);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.Answer1, 12), 12);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.EMail, 40), 40);
        WriteShortString(ms, string.Empty, 10); // sRandCode
        WriteShortString(ms, TrimToMaxGbkBytes(entry.Quiz2, 20), 20);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.Answer2, 12), 12);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.BirthDay, 10), 10);
        WriteShortString(ms, TrimToMaxGbkBytes(entry.MobilePhone, 13), 13);
        WriteShortString(ms, string.Empty, 20); // sMemo
        WriteShortString(ms, string.Empty, 20); // sL2Password
        return ms.ToArray();
    }

    private static void WriteShortString(Stream stream, string value, int maxSize)
    {
        byte[] bytes = string.IsNullOrEmpty(value) ? Array.Empty<byte>() : GbkEncoding.Instance.GetBytes(value);
        int len = Math.Min(bytes.Length, maxSize);
        stream.WriteByte(unchecked((byte)len));
        if (len > 0)
            stream.Write(bytes, 0, len);
        int pad = maxSize - len;
        if (pad > 0)
            stream.Write(new byte[pad], 0, pad);
    }

    private static string TrimToMaxGbkBytes(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        string trimmed = value.Trim();
        while (trimmed.Length > 0 && GbkEncoding.Instance.GetByteCount(trimmed) > maxBytes)
            trimmed = trimmed[..^1];
        return trimmed;
    }
}

/// <summary>注册账号用的数据结构。</summary>
public sealed record MirAccountFullEntry(
    string Account, string Password, string UserName,
    string Quiz1, string Answer1, string Quiz2, string Answer2,
    string BirthDay, string SSNo, string Phone, string EMail, string MobilePhone);
