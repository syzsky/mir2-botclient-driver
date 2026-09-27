using System.Windows;

namespace BotClientDriverHostUi.Views;

/// <summary>
/// 服务端索要密码（SM_PASSWORD，!Setup.txt 的仓库/动作保护）时的输入框。
///
/// 为什么必须有它：不应答的后果是服务端把 m_boCanWalk/Run/Hit/Spell/UseItem/Deal/Drop
/// 全部置 False，所有动作被**整包丢弃、不排队** —— 界面上仍然显示"挂机中"，角色却一动不动，
/// 而且不会有任何错误回包。这是整套静默闸门里最难发现的一种。
///
/// 按原设计密码不落盘，现问现发；窗口关闭后 Password 保留在内存里直到下一次询问。
/// </summary>
public partial class PasswordPromptWindow : Window
{
    public PasswordPromptWindow() => InitializeComponent();

    /// <summary>用户输入的密码；点"取消"时为 null。</summary>
    public string? Password { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Password = PwBox.Password;
        DialogResult = true;
    }
}
