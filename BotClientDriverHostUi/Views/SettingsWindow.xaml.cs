using System.Windows;
using BotClientDriverHost;

namespace BotClientDriverHostUi.Views;

/// <summary>
/// 设置窗口：直接编辑 HostSettings（= botsettings.json 的 schema），与无界面宿主共用同一份文件。
/// 只做“读进来 → 改 → 写回去”，不引入任何额外配置层。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly HostSettings _settings;

    public SettingsWindow(HostSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadFrom(settings);
    }

    private void LoadFrom(HostSettings s)
    {
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString();
        AccountBox.Text = s.Account;
        ServerBox.Text = s.ServerName;
        CharBox.Text = s.CharacterName;
        FightRangeBox.Text = s.FightRange.ToString();
        WalkBox.Text = s.WalkIntervalMs.ToString();
        AttackBox.Text = s.AttackIntervalMs.ToString();
        PotionBox.Text = s.PotionIntervalMs.ToString();
        PickupBox.Text = s.PickupIntervalMs.ToString();
        HpPotionBox.Text = s.HpPotionPercent.ToString();
        MpPotionBox.Text = s.MpPotionPercent.ToString();
        EscapeBox.Text = s.EscapeHpPercent.ToString();
        AutoPickupCheck.IsChecked = s.AutoPickup;
        HumanCheck.IsChecked = s.Human?.Enabled ?? true;
        SkillsCheck.IsChecked = s.Skills?.Enabled ?? false;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _settings.Host = HostBox.Text.Trim();
        _settings.Port = ParseInt(PortBox.Text, _settings.Port);
        _settings.Account = AccountBox.Text.Trim();
        _settings.ServerName = ServerBox.Text.Trim();
        _settings.CharacterName = CharBox.Text.Trim();
        _settings.FightRange = ParseInt(FightRangeBox.Text, _settings.FightRange);
        _settings.WalkIntervalMs = ParseInt(WalkBox.Text, _settings.WalkIntervalMs);
        _settings.AttackIntervalMs = ParseInt(AttackBox.Text, _settings.AttackIntervalMs);
        _settings.PotionIntervalMs = ParseInt(PotionBox.Text, _settings.PotionIntervalMs);
        _settings.PickupIntervalMs = ParseInt(PickupBox.Text, _settings.PickupIntervalMs);
        _settings.HpPotionPercent = ParseInt(HpPotionBox.Text, _settings.HpPotionPercent);
        _settings.MpPotionPercent = ParseInt(MpPotionBox.Text, _settings.MpPotionPercent);
        _settings.EscapeHpPercent = ParseInt(EscapeBox.Text, _settings.EscapeHpPercent);
        _settings.AutoPickup = AutoPickupCheck.IsChecked == true;

        _settings.Human ??= new BotClient.Human.HumanTuning();
        _settings.Human.Enabled = HumanCheck.IsChecked == true;

        _settings.Skills ??= new BotClient.Session.Combat.SkillRotationPlan();
        _settings.Skills.Enabled = SkillsCheck.IsChecked == true;

        DialogResult = true;
    }

    private static int ParseInt(string text, int fallback)
    {
        return int.TryParse((text ?? string.Empty).Trim(), out int value) ? value : fallback;
    }
}
