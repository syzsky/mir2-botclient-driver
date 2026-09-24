using System.Diagnostics.CodeAnalysis;

namespace BotClient.Protocol;

/// <summary>
/// 协议常量(从原始客户端的 MirClient.Protocol.Grobal2 同步, 2026-07重构)。
/// 涵盖登录/选角/移动/战斗/物品/交互所需的完整常量。
/// </summary>
public static class Grobal2
{
    // ============================================================
    //  基础常量
    // ============================================================
    public const int MapNameLen = 16;
    public const int ActorNameLen = 14;
    public const int ItemNameLen = 20;
    public const int GuildNameLen = 20;

    public const int VERSION_NUMBER = 20020522;
    public const int CLIENT_VERSION_NUMBER = 120040918;
    public const int RUNLOGINCODE = 0;

    // ---- CM_LOGINNOTICEOK 字段 (原版 ClMain.pas:29507) ----
    /// <summary>TClientVersion 默认值 cvSerial (Share.pas:34);M2 取 LoByte 作 TClientUIType,合法域 0..6。</summary>
    public const byte CLIENT_UI_TYPE = 3;
    public const int SCREEN_WIDTH = 1024;
    public const int SCREEN_HEIGHT = 768;
    /// <summary>WIDTHGRIDCOUNT = Round((1024+64)/48)=23 → 取偶 24 → div 2 = 12 (ClMain.pas:5559-5563)。</summary>
    public const byte VIEW_GRID_COUNT = 12;
    public const int DEFBLOCKSIZE = 22;

    // ============================================================
    //  RunGate 专用协议常量 (gxx: GateShare.pas / Grobal2_Ex.pas)
    // ============================================================
    /// <summary>RunGate 下行二进制包头签名 (TRungateMsgHeader.Code)。</summary>
    public const uint RUN_GATE_MSG_CODE = 0xAABBCCDD;
    /// <summary>RunGate 二进制头长度: Code(4)+DataLen(4)+Msg(16)。</summary>
    public const int RUN_GATE_HEADER_SIZE = 24;
    /// <summary>RunGate 下行空闲探测 (SM_CHECK_RUNGATE 客户端可见值)。</summary>
    public const int SM_RUNGATE_HEARTBEAT = 112;
    /// <summary>RunGate 客户端保活应答 (上行文本帧 Ident)。</summary>
    public const int CM_RUNGATE_HEARTBEAT = 500;

    public const int UNITX = 48;
    public const int UNITY = 32;
    public const int HALFX = 24;
    public const int HALFY = 16;

    public const int MAXBAGITEM = 52;
    public const int MAX_OVERLAPITEM = 9999;
    public const int HOWMANYMAGICS = 20;

    // ============================================================
    //  方向 (DR_*)
    // ============================================================
    public const byte DR_UP = 0;
    public const byte DR_UPRIGHT = 1;
    public const byte DR_RIGHT = 2;
    public const byte DR_DOWNRIGHT = 3;
    public const byte DR_DOWN = 4;
    public const byte DR_DOWNLEFT = 5;
    public const byte DR_LEFT = 6;
    public const byte DR_UPLEFT = 7;

    // ============================================================
    //  攻击模式 (HAM_*)
    // ============================================================
    public const int HAM_ALL = 0;
    public const int HAM_PEACE = 1;
    public const int HAM_DEAR = 2;
    public const int HAM_MASTER = 3;
    public const int HAM_GROUP = 4;
    public const int HAM_GUILD = 5;
    public const int HAM_PKATTACK = 6;

    // ============================================================
    //  角色/怪物类型 (RC_*)
    // ============================================================
    public const byte RC_PLAYOBJECT = 0;
    public const byte RC_HEROOBJECT = 1;
    public const byte RC_NPC = 10;
    public const byte RC_GUARD = 11;
    public const byte RC_PEACENPC = 15;
    public const byte RC_ANIMAL = 50;
    public const byte RC_EXERCISE = 55;
    public const byte RC_PLAYCLONE = 60;
    public const byte RC_MONSTER = 80;
    public const byte RC_ARCHERGUARD = 112;
    public const byte RC_PLAYMOSTER = 150;    // 人形怪,可攻击

    // ============================================================
    //  SM_TURN 包体布局 (ObjPlayer.pas:36854-36884 ServerSendTurn)
    //  TCharDesc(Grobal2.pas:3521 packed) + Feature 缓冲 + EncodeString(名字/颜色)
    // ============================================================
    /// <summary>TCharDesc: Feature 长度(Byte) + m_nCharStatus(Int64) + MagicLevel(Int32)。</summary>
    public const int CHAR_DESC_SIZE = 13;
    /// <summary>TMonFeature(Grobal2.pas:3397) 字节数;抓包实测人物 THumFeature 远大于此值,靠长度区分两种外观结构。</summary>
    public const int MON_FEATURE_SIZE = 19;
    /// <summary>TMonFeature.btRace 偏移 = wRaceImg(2)+wWeapon(2)+wAppr(2)+btBodyColor(1)。</summary>
    public const int MON_FEATURE_RACE_OFF = 7;

    // 兼容旧名 (BotClient 早期版本)
    // 注意:值与服务端 SystemModule/Grobal2.cs、原版客户端保持一致:
    //   RCC_USERHUMAN=0  RCC_GUARD=12  RCC_MERCHANT=50
    // 之前误把 RCC_MERCHANT 写成 10、RCC_GUARD 写成 11,
    // 导致商人(50)/守卫(12)被判成怪物、怪物(10/11/15)被判成 NPC。
    public const byte RCC_USERHUMAN = RC_PLAYOBJECT;
    public const byte RCC_GUARD = 12;
    public const byte RCC_MERCHANT = 50;

    // ============================================================
    //  装备部位 (U_*) — Grobal2.pas:99..127;0..17 为真实装备位,18..29 为时装位
    // ============================================================
    /// <summary>THumanUseItems 上界 (Grobal2.pas:51)。</summary>
    public const int MAX_USE_ITEM_COUNT = 30;
    /// <summary>人物背包基础格数 (Grobal2.pas:39 DEF_MAX_BAG_ITEM)。
    /// 服务端算上限时只加"NPC 脚本开的扩展格":TPlayObject.GetMaxBagCount
    /// = DEF_MAX_BAG_ITEM + m_btExtBagOpenItemCount - 交易占用(ObjPlayer.pas:13677)。</summary>
    public const int DEF_MAX_BAG_ITEM = 46;
    public const int U_DRESS = 0;
    public const int U_WEAPON = 1;
    public const int U_RIGHTHAND = 2;
    public const int U_NECKLACE = 3;
    public const int U_HELMET = 4;
    public const int U_ARMRINGL = 5;
    public const int U_ARMRINGR = 6;
    public const int U_RINGL = 7;
    public const int U_RINGR = 8;
    public const int U_BUJUK = 9;
    public const int U_BELT = 10;
    public const int U_BOOTS = 11;
    public const int U_CHARM = 12;
    public const int U_HAT = 13;
    public const int U_DRUM = 14;
    public const int U_HORSE = 15;
    public const int U_SHIELD = 16;
    public const int U_JADE = 17;

    // ============================================================
    //  ★ 客户端→服务端 (CM_*) — 登录阶段 (LoginGate)
    // ============================================================
    public const ushort CM_PROTOCOL = 2000;          // 协议版本协商 ★
    public const ushort CM_IDPASSWORD = 2001;        // 账号密码登录
    public const ushort CM_ADDNEWUSER = 2002;        // 注册账号
    public const ushort CM_CHANGEPASSWORD = 2003;    // 修改密码
    public const ushort CM_UPDATEUSER = 2004;        // 更新账号

    // ============================================================
    //  ★ 客户端→服务端 (CM_*) — 选角阶段 (SelGate)
    // ============================================================
    public const ushort CM_QUERYCHR = 100;           // 查询角色列表
    public const ushort CM_NEWCHR = 101;             // 创建角色
    public const ushort CM_DELCHR = 102;             // 删除角色
    public const ushort CM_SELCHR = 103;             // 选择角色
    public const ushort CM_SELECTSERVER = 104;       // 选择服务器
    public const ushort CM_QUERYDELCHR = 105;        // 查询已删角色
    public const ushort CM_GETBACKDELCHR = 106;      // 恢复已删角色
    public const ushort CM_RECONNECT = 107;          // 断线重连

    // ============================================================
    //  ★ 客户端→服务端 (CM_*) — 游戏中查询
    // ============================================================
    public const ushort CM_QUERYUSERNAME = 80;       // 查询名称/NPC交互
    public const ushort CM_QUERYBAGITEMS = 81;       // 查询背包
    public const ushort CM_QUERYUSERSTATE = 82;      // 查询角色状态
    public const ushort CM_HIDEDEATHBODY = 1200;     // 隐藏尸体(用这个值,不是CM_TURN)

    // ============================================================
    //  ★ 客户端→服务端 (CM_*) — 动作/移动 (RunGate) [30xx系列]
    // ============================================================
    public const ushort CM_THROW = 3005;             // 投掷
    public const ushort CM_TURN = 3010;              // ★ 转向 (注意:不是1200!)
    public const ushort CM_WALK = 3011;              // ★ 行走 (注意:不是1201!)
    public const ushort CM_SITDOWN = 3012;           // 坐下
    public const ushort CM_RUN = 3013;               // ★ 跑步 (注意:不是1202!)
    public const ushort CM_HIT = 3014;               // ★ 普通攻击 (注意:不是1100!)
    public const ushort CM_HEAVYHIT = 3015;          // 重击
    public const ushort CM_BIGHIT = 3016;            // 大攻击
    public const ushort CM_SPELL = 3017;             // ★ 释放魔法
    public const ushort CM_POWERHIT = 3018;          // 强力攻击
    public const ushort CM_LONGHIT = 3019;           // 远程攻击
    public const ushort CM_WIDEHIT = 3024;           // 范围攻击
    public const ushort CM_FIREHIT = 3025;           // 火焰攻击
    public const ushort CM_SAY = 3030;               // ★ 说话/聊天
    /// <summary>回答服务端的 SM_PASSWORD 密码框(仓库/动作保护)。
    /// 服务端 UsrEngn.pas:5721 对 CM_PASSWORD 特殊映射 wParam=DefMsg.Param,
    /// ObjPlayer.pas:25831 判 `ProcessMsg.wParam = 0` → 只把包当成"再问一遍"(@开锁),
    /// 所以真答案必须放 Param=1(原版 ClMain.pas:16811 SendPassword(Str,1)),包体=密码。</summary>
    public const ushort CM_PASSWORD = 1105;          // 与 SM_PASSWORD 同号(Grobal2.pas:344)
    public const ushort CM_HORSERUN = 3035;          // 骑马跑
    public const ushort CM_CRSHIT = 3036;            // 十字攻击
    public const ushort CM_TWINHIT = 3038;           // 双斩攻击

    // ============================================================
    //  ★ 客户端→服务端 (CM_*) — 游戏中交互/物品
    // ============================================================
    public const ushort CM_DROPITEM = 1000;          // 丢弃物品
    public const ushort CM_PICKUP = 1001;            // ★ 拾取物品
    public const ushort CM_OPENDOOR = 1002;          // 开门
    public const ushort CM_TAKEONITEM = 1003;        // ★ 穿戴装备
    public const ushort CM_TAKEOFFITEM = 1004;       // ★ 卸下装备
    public const ushort CM_HEROMAGICKEYCHANGE = 1005;
    public const ushort CM_EAT = 1006;               // 使用物品/药品
    public const ushort CM_BUTCH = 1007;             // 挖肉
    public const ushort CM_MAGICKEYCHANGE = 1008;    // 魔法键设置
    public const ushort CM_SOFTCLOSE = 1009;         // ★ 小退
    public const ushort CM_CLICKNPC = 1010;          // ★ 点击NPC
    public const ushort CM_MERCHANTDLGSELECT = 1011; // NPC对话选择
    public const ushort CM_MERCHANTQUERYSELLPRICE = 1012;
    public const ushort CM_USERSELLITEM = 1013;      // 卖给NPC
    public const ushort CM_USERBUYITEM = 1014;       // 从NPC购买
    public const ushort CM_USERGETDETAILITEM = 1015;
    public const ushort CM_DROPGOLD = 1016;          // 丢金币
    public const ushort CM_LOGINNOTICEOK = 1018;     // ★ 确认公告
    public const ushort CM_GROUPMODE = 1019;         // 组队模式
    public const ushort CM_CREATEGROUP = 1020;
    public const ushort CM_ADDGROUPMEMBER = 1021;
    public const ushort CM_DELGROUPMEMBER = 1022;
    public const ushort CM_USERREPAIRITEM = 1023;    // 修理物品
    public const ushort CM_MERCHANTQUERYREPAIRCOST = 1024; // 向NPC查询修理费用
    public const ushort CM_DEALTRY = 1025;           // 请求交易
    public const ushort CM_DEALADDITEM = 1026;
    public const ushort CM_DEALDELITEM = 1027;
    public const ushort CM_DEALCANCEL = 1028;
    public const ushort CM_DEALCHGGOLD = 1029;
    public const ushort CM_DEALEND = 1030;
    public const ushort CM_USERSTORAGEITEM = 1031;   // 仓库存储
    public const ushort CM_USERTAKEBACKSTORAGEITEM = 1032;
    public const ushort CM_WANTMINIMAP = 1033;       // 请求小地图
    public const ushort CM_USERMAKEDRUGITEM = 1034;
    public const ushort CM_OPENGUILDDLG = 1035;       // ★ 打开行会面板
    public const ushort CM_GUILDHOME = 1036;          // 行会主页
    public const ushort CM_GUILDMEMBERLIST = 1037;    // ★ 行会成员列表
    public const ushort CM_GUILDADDMEMBER = 1038;     // 行会加人
    public const ushort CM_GUILDDELMEMBER = 1039;     // 行会踢人
    public const ushort CM_GUILDUPDATENOTICE = 1040;  // 更新行会公告
    public const ushort CM_GUILDUPDATERANKINFO = 1041;// 更新排名信息
    public const ushort CM_GUILDALLY = 1044;          // 结盟
    public const ushort CM_GUILDBREAKALLY = 1045;     // 解除结盟
    public const ushort CM_RECALLHERO = 1050;         // 召唤英雄
    public const ushort CM_UNRECALLHERO = 1051;
    public const ushort CM_SETSERIESSKILL = 1052;    // 合击技能设置
    public const ushort CM_FIRESERIESSKILL = 1053;   // 释放合击
    public const ushort CM_TRAINSKILL = 1063;         // 修炼技能
    public const ushort CM_QUERYVAL = 1065;           // 查询值

    // ============================================================
    //  ★ 服务端→客户端 (SM_*) — 登录结果
    // ============================================================
    public const ushort SM_CERTIFICATION_SUCCESS = 500;
    public const ushort SM_CERTIFICATION_FAIL = 501;
    public const ushort SM_ID_NOTFOUND = 502;
    public const ushort SM_PASSWD_FAIL = 503;
    public const ushort SM_NEWID_SUCCESS = 504;
    public const ushort SM_NEWID_FAIL = 505;
    public const ushort SM_CHGPASSWD_SUCCESS = 506;
    public const ushort SM_CHGPASSWD_FAIL = 507;

    public const ushort SM_QUERYDELCHR = 518;
    public const ushort SM_GETBACKDELCHR = 519;
    public const ushort SM_QUERYCHR = 520;            // 角色列表
    public const ushort SM_NEWCHR_SUCCESS = 521;
    public const ushort SM_NEWCHR_FAIL = 522;
    public const ushort SM_DELCHR_SUCCESS = 523;
    public const ushort SM_DELCHR_FAIL = 524;
    public const ushort SM_STARTPLAY = 525;            // ★ 开始游戏(含RunGate地址)
    public const ushort SM_STARTFAIL = 526;
    public const ushort SM_QUERYCHR_FAIL = 527;
    public const ushort SM_OUTOFCONNECTION = 528;      // 被踢下线
    public const ushort SM_PASSOK_SELECTSERVER = 529;  // ★ 登录成功+服务器列表
    public const ushort SM_SELECTSERVER_OK = 530;      // ★ 选服成功+SelGate地址
    public const ushort SM_NEEDUPDATE_ACCOUNT = 531;

    public const ushort SM_VERSION_FAIL = 1106;        // 版本不匹配
    public const ushort SM_OVERCLIENTCOUNT = 1109;     // 客户端超限
    public const ushort SM_CDVERSION_FAIL = 1307;      // CD版本检查失败

    // ============================================================
    //  ★ 服务端→客户端 (SM_*) — 游戏中核心消息
    // ============================================================
    public const ushort SM_LOGON = 50;                 // ★ 进入游戏 (坐标在Header中)
    public const ushort SM_NEWMAP = 51;                // ★ 切换地图
    public const ushort SM_ABILITY = 52;               // ★ 角色属性 (注意:不是53!)
    public const ushort SM_HEALTHSPELLCHANGED = 53;    // HP/MP变化
    public const ushort SM_MAPDESCRIPTION = 54;        // 地图描述
    public const ushort SM_WITHINRANGE = 56;            // ★ 对象进入视野
    public const ushort SM_POSTIONMOVE = 57;            // ★ 位置移动 (注意:不是60!)
    public const ushort SM_DAYCHANGING = 46;            // 昼夜变化

    // 动作消息 (10-49)
    public const ushort SM_TURN = 10;
    public const ushort SM_WALK = 11;
    public const ushort SM_SITDOWN = 12;
    public const ushort SM_RUN = 13;
    public const ushort SM_HIT = 14;
    public const ushort SM_HEAVYHIT = 15;
    public const ushort SM_BIGHIT = 16;
    public const ushort SM_SPELL = 17;
    public const ushort SM_POWERHIT = 18;
    public const ushort SM_LONGHIT = 19;
    public const ushort SM_DIGUP = 20;
    public const ushort SM_DIGDOWN = 21;
    public const ushort SM_FLYAXE = 22;
    public const ushort SM_LIGHTING = 23;
    public const ushort SM_WIDEHIT = 24;
    /// <summary>复活广播(服务端 TBaseObject.ReAlive 发 RM_ALIVE)。没有对应的客户端上行包:
    /// 复活只能由服务端触发(脚本 RELIVE/GM/复活术),或断开重连时由服务端在家点重开角色。</summary>
    public const ushort SM_ALIVE = 27;
    public const ushort SM_MOVEFAIL = 28;
    public const ushort SM_HIDE = 29;
    public const ushort SM_DISAPPEAR = 30;
    public const ushort SM_STRUCK = 31;                // ★ 受击
    /// <summary>"有人倒下"。⚠ 真死的对象(包括玩家自己)收到的是 **SM_NOWDEATH(34)**,
    /// 这条只在"新观众刚看见一具已存在的尸体"时补发(ObjPlayer.pas:15940-15955)。</summary>
    public const ushort SM_DEATH = 32;                 // ★ 死亡(已倒地的对象)
    public const ushort SM_SKELETON = 33;
    /// <summary>★ 当场倒下的那一条:Die 发 SendRefMsg(RM_DEATH,...,nParam3=1) → 摆成 34 发给
    /// 视野里的每个人,广播循环没有 &lt;&gt; Self 排除 ⇒ 死者自己也收 34(ObjBase.pas:42849 / ObjPlayer.pas:37959)。</summary>
    public const ushort SM_NOWDEATH = 34;
    public const ushort SM_RUSH = 6;
    public const ushort SM_BACKSTEP = 9;
    public const ushort SM_FIREHIT = 8;
    public const ushort SM_HEAR = 40;                  // 听到聊天
    public const ushort SM_FEATURECHANGED = 41;        // ★ 外观变化
    public const ushort SM_USERNAME = 42;              // 用户名查询结果
    public const ushort SM_WINEXP = 44;                // ★ 获得经验
    public const ushort SM_LEVELUP = 45;               // ★ 升级

    // ============================================================
    //  ★ 服务端→客户端 (SM_*) — 物品/装备
    // ============================================================
    public const ushort SM_ADDITEM = 200;              // 背包添加物品
    public const ushort SM_BAGITEMS = 201;             // ★ 背包物品列表 (注意:不是62!)
    public const ushort SM_DELITEM = 202;              // 删除物品(单件,Recog=MakeIndex)
    public const ushort SM_DELITEMS = 709;             // 批量删除物品(gxx 脚本/商店用)
    public const ushort SM_UPDATEITEM = 203;           // 更新物品
    public const ushort SM_ADDMAGIC = 210;             // 添加技能
    public const ushort SM_SENDMYMAGIC = 211;          // 发送技能列表
    public const ushort SM_DELMAGIC = 212;             // 删除技能
    public const ushort SM_QUERYVALUE = 215;           // 查询值

    public const ushort SM_DROPITEM_SUCCESS = 600;
    public const ushort SM_DROPITEM_FAIL = 601;
    public const ushort SM_ITEMSHOW = 610;             // ★ 地面显示物品
    public const ushort SM_ITEMHIDE = 611;             // ★ 地面隐藏物品
    public const ushort SM_OPENDOOR_OK = 612;
    public const ushort SM_OPENDOOR_LOCK = 613;
    public const ushort SM_CLOSEDOOR = 614;
    public const ushort SM_TAKEON_OK = 615;            // 穿戴成功
    public const ushort SM_TAKEON_FAIL = 616;          // 穿戴失败
    public const ushort SM_TAKEOFF_OK = 619;           // 卸下成功
    public const ushort SM_TAKEOFF_FAIL = 620;         // 卸下失败
    public const ushort SM_SENDUSEITEMS = 621;         // ★ 已装备物品 (注意:不是61!)
    public const ushort SM_WEIGHTCHANGED = 622;        // 负重变化
    public const ushort SM_CLEAROBJECTS = 633;         // ★ 清空视野对象
    public const ushort SM_CHANGEMAP = 634;            // 地图切换(与SM_NEWMAP并存)
    public const ushort SM_EAT_OK = 635;               // 使用物品成功
    public const ushort SM_EAT_FAIL = 636;             // 使用物品失败
    public const ushort SM_BUTCH = 637;
    public const ushort SM_MAGICFIRE = 638;            // 魔法释放效果
    public const ushort SM_MAGIC_LVEXP = 640;          // 技能经验
    public const ushort SM_DURACHANGE = 642;           // 持久变化(按装备槽)
    //  gxx 的"按 MakeIndex 更新持久"通道(Grobal2.pas:2127)。和 642 不重复:642 只认身上装备槽,
    //  魔法耗持久(Magic.pas:690)、NPC 修理/强化(NpcActionCmd.pas:297)按物品本身回写走这一条。
    public const ushort SM_UPDATEITEM_DURA = 10325;
    public const ushort SM_UPDATEITEM_DURAMAX = 10326;
    //  NPC 脚本开通/改变背包扩展格时推一条(ClMain.pas:35117 用 Param=页数、Tag=扩展格数)。
    //  登录时服务端**不发**这条,所以背包上限要从 DEF_MAX_BAG_ITEM 起算,收到才往上加。
    public const ushort SM_EXT_BAG_COUNT_CHANGE = 10409;
    public const ushort SM_MERCHANTSAY = 643;          // NPC说话
    public const ushort SM_SENDUSERSTORAGEITEM = 700;  // ★ 仓库存储结果(带仓库物品列表?)
    public const ushort SM_STORAGE_OK = 701;           // 存物品成功
    public const ushort SM_STORAGE_FULL = 702;         // 仓库已满
    public const ushort SM_STORAGE_FAIL = 703;         // 存物品失败
    public const ushort SM_SAVEITEMLIST = 704;         // ★ 仓库物品列表
    public const ushort SM_TAKEBACKSTORAGEITEM_OK = 705;   // 取物品成功
    public const ushort SM_TAKEBACKSTORAGEITEM_FAIL = 706; // 取物品失败
    public const ushort SM_TAKEBACKSTORAGEITEM_FULLBAG = 707; // 背包已满
    public const ushort SM_DEALMENU = 673;            // ★ 交易窗口打开(body=对方玩家名)
    public const ushort SM_DEALTRY_FAIL = 674;        // 交易请求失败
    public const ushort SM_DEALADDITEM_OK = 675;      // 放入物品成功
    public const ushort SM_DEALADDITEM_FAIL = 676;    // 放入物品失败
    public const ushort SM_DEALDELITEM_OK = 677;      // 取回物品成功
    public const ushort SM_DEALDELITEM_FAIL = 678;    // 取回物品失败
    public const ushort SM_DEALCANCEL = 681;          // 交易取消
    public const ushort SM_DEALREMOTEADDITEM = 682;   // ★ 对方放入物品(body=物品, Recog=对方ID)
    public const ushort SM_DEALREMOTEDELITEM = 683;   // 对方取回物品
    public const ushort SM_DEALCHGGOLD_OK = 684;      // 放入金币成功
    public const ushort SM_DEALCHGGOLD_FAIL = 685;    // 放入金币失败
    public const ushort SM_DEALREMOTECHGGOLD = 686;   // 对方放入金币(Recog=金币数)
    public const ushort SM_DEALSUCCESS = 687;         // ★ 交易成功
    public const ushort SM_MENU_OK = 767;              // ★ NPC脚本菜单结果/提示文本(如"等级不足"等)
    public const ushort SM_MERCHANTDLGCLOSE = 644;     // NPC对话关闭
    public const ushort SM_SENDGOODSLIST = 645;        // NPC商品列表
    public const ushort SM_SENDUSERSELL = 646;         // ★ 进入出售界面(出售列表)
    public const ushort SM_SENDBUYPRICE = 647;         // 出售价格查询结果(Recog=价格)
    public const ushort SM_USERSELLITEM_OK = 648;      // 出售成功(Recog=金币)
    public const ushort SM_USERSELLITEM_FAIL = 649;    // 出售失败(Recog=代码)
    public const ushort SM_BUYITEM_SUCCESS = 650;      // 购买成功(Recog=金币, Param|Tag=物品ID)
    public const ushort SM_BUYITEM_FAIL = 651;         // 购买失败(Recog=代码:1失败/2负重不足/3金币不足)
    public const ushort SM_SENDDETAILGOODSLIST = 652;  // 商品详情列表(子菜单)
    public const ushort SM_GOLDCHANGED = 653;          // ★ 金币变化
    public const ushort SM_CHANGELIGHT = 654;          // 光源变化
    public const ushort SM_LAMPCHANGEDURA = 655;
    public const ushort SM_CHANGENAMECOLOR = 656;      // 名字颜色
    public const ushort SM_CHARSTATUSCHANGED = 657;    // 状态变化(中毒/防等)
    public const ushort SM_SENDNOTICE = 658;           // ★ 游戏公告
    public const ushort SM_GROUPMODECHANGED = 659;     // 组队模式变化
    public const ushort SM_CREATEGROUP_OK = 660;       // 建组成功
    public const ushort SM_CREATEGROUP_FAIL = 661;     // 建组失败
    public const ushort SM_GROUPADDMEM_OK = 662;       // 加人成功
    public const ushort SM_GROUPDELMEM_OK = 663;       // 踢人成功
    public const ushort SM_GROUPADDMEM_FAIL = 664;     // 加人失败
    public const ushort SM_GROUPDELMEM_FAIL = 665;     // 踢人失败
    public const ushort SM_GROUPCANCEL = 666;          // 组队取消
    public const ushort SM_GROUPMEMBERS = 667;         // ★ 组队成员列表
    public const ushort SM_SENDUSERREPAIR = 668;       // ★ 进入修理界面(Recog=merchantId)
    public const ushort SM_USERREPAIRITEM_OK = 669;    // 修理成功(Recog=剩余金币, Param=新Dura, Tag=新DuraMax)
    public const ushort SM_USERREPAIRITEM_FAIL = 670;  // 修理失败
    public const ushort SM_SENDREPAIRCOST = 671;       // 修理费用查询结果(Recog=价格, -1=不可修理)
    public const ushort SM_SENDUSERSTATE = 751;        // ★ 用户完整状态(二进制)
    public const ushort SM_SUBABILITY = 752;           // 攻击/魔法/道术上下限
    public const ushort SM_OPENGUILDDLG = 753;         // ★ 打开行会面板(body=行会信息)
    public const ushort SM_OPENGUILDDLG_FAIL = 754;    // 打开行会面板失败
    public const ushort SM_SENDGUILDMEMBERLIST = 756;  // ★ 行会成员列表
    public const ushort SM_GUILDADDMEMBER_OK = 757;    // 行会加人成功
    public const ushort SM_GUILDADDMEMBER_FAIL = 758;  // 行会加人失败
    public const ushort SM_GUILDDELMEMBER_OK = 759;    // 行会踢人成功
    public const ushort SM_GUILDDELMEMBER_FAIL = 760;  // 行会踢人失败
    public const ushort SM_GUILDRANKUPDATE_FAIL = 761; // 排名更新失败
    public const ushort SM_BUILDGUILD_OK = 762;        // 建行会成功
    public const ushort SM_BUILDGUILD_FAIL = 763;      // 建行会失败
    public const ushort SM_CHANGEGUILDNAME = 750;      // 行会名称变化
    public const ushort SM_GUILDMAKEALLY_OK = 768;     // 结盟成功
    public const ushort SM_GUILDMAKEALLY_FAIL = 769;   // 结盟失败
    public const ushort SM_GUILDBREAKALLY_OK = 770;    // 解除结盟成功
    public const ushort SM_GUILDBREAKALLY_FAIL = 771;  // 解除结盟失败

    // ============================================================
    //  ★ 服务端→客户端 (SM_*) — 消息/聊天
    // ============================================================
    public const ushort SM_SYSMESSAGE = 100;           // 系统消息
    public const ushort SM_GROUPMESSAGE = 101;         // 组队消息
    public const ushort SM_CRY = 102;                  // 喊话
    public const ushort SM_WHISPER = 103;              // 悄悄话
    public const ushort SM_GUILDMESSAGE = 104;         // 行会消息
    // 滚动/渐隐公告族(Source/Common/Grobal2.pas:1495-1498)。服务端一律
    // SendSocket(@DefMsg, ProcessMsg.sMsg) 原样 GBK,不走 EdCode (ObjPlayer.pas:37758+ 各 ServerSend*Message)。
    public const ushort SM_NEWLINEMESSAGE = 96;        // 换行消息
    public const ushort SM_SUPERMOVEMESSAGE = 97;      // 顶部渐隐消息(如 [进入安全区])
    public const ushort SM_SCREENMESSAGE = 98;         // 屏幕消息
    public const ushort SM_MOVEMESSAGE = 99;           // 跑马灯滚动消息
    // 110/111 不是消息弹道:Grobal2.pas:1420-1421 是 SM_ACTION_RET(动作回执,带服务端真实坐标)
    // 与 SM_MAGIC_OPEN(技能开关)。上行 110 是 CM_GETUSERSHOPS(盛大店铺,gxx 未实现),我方不发。
    public const ushort SM_ACTION_RET = 110;           // 动作回执 / 超速拒绝时的坐标回正
    public const ushort SM_MAGIC_OPEN = 111;           // 技能开关状态,本 bot 不用

    // ============================================================
    //  ★ 服务端→客户端 (SM_*) — 英雄
    // ============================================================
    public const ushort SM_HEROLOGOUT = 896;
    public const ushort SM_HEROLOGIN = 897;
    public const ushort SM_HERONAME = 898;
    public const ushort SM_HEROSTATE = 899;
    public const ushort SM_HEROABILITY = 900;
    public const ushort SM_HEROBAGITEMS = 902;
    public const ushort SM_HEROUSEITEMS = 903;
    public const ushort SM_HEROMYMAGICS = 904;
    public const ushort SM_HEROLEVELUP = 914;
    public const ushort SM_HEROWINEXP = 915;

    // ============================================================
    //  ★ 服务端→客户端 (SM_*) — 杂项
    // ============================================================
    public const ushort SM_RECONNECT = 802;            // 重连通知
    public const ushort SM_GHOST = 803;                // 隐身模式
    public const ushort SM_SHOWEVENT = 804;            // 显示地图事件
    public const ushort SM_HIDEEVENT = 805;            // 隐藏地图事件
    public const ushort SM_SPACEMOVE_HIDE = 800;       // 空间移动隐藏
    public const ushort SM_SPACEMOVE_SHOW = 801;       // 空间移动显示
    public const ushort SM_OPENHEALTH = 1100;          // 开启血条
    public const ushort SM_CLOSEHEALTH = 1101;         // 关闭血条
    public const ushort SM_PASSWORD = 1105;            // 仓库密码
    public const ushort SM_PLAYSOUND = 1110;           // 播放声音
    public const ushort SM_LEVELRANK = 1108;           // 排行榜
    public const ushort SM_PICKUP_FAIL = 558;          // 拾取失败
    public const ushort SM_SERVERCONFIG = 5007;        // 服务端配置
    /// <summary>游戏币名称列表(元宝/游戏点/金币…),抓包 rec15 实测 ident=55 且包体 6bit。
    /// 不是 5008 —— gxx 里 5008 是 SM_CHECK_RUNGATE1(网关校验1)/CM_TAKEOFFJEWELRY。</summary>
    public const ushort SM_GAMEGOLDNAME = 55;

    // ============================================================
    //  杂项常量
    // ============================================================
    public const string sSTRING_GOLDNAME = "金币";

    // ============================================================
    //  消息内部 ID('+')
    // ============================================================
    public const ushort ActMessage = 0xFFFF;
}

public enum MirSessionStage
{
    None,
    LoginGate,
    SelectServer,
    SelGate,
    RunGate,
    Playing
}
