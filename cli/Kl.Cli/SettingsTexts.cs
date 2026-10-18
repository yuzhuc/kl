namespace Kl.Cli;

public static class SettingsTexts
{
    public static string T(UiLanguage language, string english, string chinese) =>
        language == UiLanguage.ChineseSimplified ? chinese : english;

    public static string Title(UiLanguage language) =>
        T(language, "KL Settings", "KL 设置");

    public static string Groups(UiLanguage language) =>
        T(language, "Groups", "分组");

    public static string Group(SettingsGroup group, UiLanguage language) =>
        group switch
        {
            SettingsGroup.Model => T(language, "Model", "模型"),
            SettingsGroup.Agent => T(language, "Agent", "Agent"),
            SettingsGroup.Plugins => T(language, "Plugins", "插件"),
            SettingsGroup.Skills => T(language, "Skills", "Skills"),
            SettingsGroup.Config => T(language, "Config", "配置"),
            _ => group.ToString()
        };

    public static string Footer(UiLanguage language) =>
        T(
            language,
            "↑/↓ move  Tab/Shift+Tab pane  Enter edit/confirm  Esc back  Ctrl+S save  F2 save & exit",
            "↑/↓ 移动  Tab/Shift+Tab 切换  Enter 编辑/确认  Esc 返回  Ctrl+S 保存  F2 保存并退出");

    public static string PluginFooter(UiLanguage language) =>
        T(
            language,
            "↑/↓ move  Tab/Shift+Tab pane  e enable  d disable  r reload  Enter confirm  Esc back  F2 save & exit",
            "↑/↓ 移动  Tab/Shift+Tab 切换  e 启用  d 禁用  r 重载  Enter 确认  Esc 返回  F2 保存并退出");

    public static string ComingSoon(UiLanguage language) =>
        T(language, "Coming soon.", "即将推出。");
}
