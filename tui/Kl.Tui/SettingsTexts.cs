namespace Kl.Tui;

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

    public static string AgentMenu(UiLanguage language) =>
        T(language, "Agent", "Agent");

    public static string AgentMenuIntro(UiLanguage language) =>
        T(
            language,
            "Configure permissions and reasoning for the active agent.",
            "配置当前 Agent 的权限与推理设置。");

    public static string Permissions(UiLanguage language) =>
        T(language, "Permissions", "权限");

    public static string PermissionsIntro(UiLanguage language) =>
        T(
            language,
            "Choose how tool actions are approved before they run.",
            "选择工具操作执行前的批准方式。");

    public static string ApprovalMode(UiLanguage language) =>
        T(language, "Approval mode", "批准模式");

    public static string SandboxBackend(UiLanguage language) =>
        T(language, "Sandbox backend", "沙箱后端");

    public static string SandboxStrict(UiLanguage language) =>
        T(language, "Strict", "严格模式");

    public static string Reasoning(UiLanguage language) =>
        T(language, "Reasoning", "推理");

    public static string ReasoningIntro(UiLanguage language) =>
        T(
            language,
            "Choose the reasoning effort for the active provider/model.",
            "选择当前供应商/模型的推理强度。");

    public static string ReasoningEffort(UiLanguage language) =>
        T(language, "Reasoning effort", "推理强度");

    public static string ReasoningSupport(UiLanguage language) =>
        T(language, "Reasoning support", "推理支持");

    public static string Supported(UiLanguage language) =>
        T(language, "supported", "支持");

    public static string Unsupported(UiLanguage language) =>
        T(language, "not supported", "不支持");

    public static string Unknown(UiLanguage language) =>
        T(language, "unknown", "未知");

    public static string QuickSelect(UiLanguage language) =>
        T(language, "Quick model select", "快速选择模型");

    public static string QuickSelectIntro(UiLanguage language) =>
        T(
            language,
            "Only models from enabled providers are shown.",
            "仅显示已启用供应商的模型。");

    public static string ConfigPath(UiLanguage language) =>
        T(language, "Config path", "配置路径");

    public static string UiConfigPath(UiLanguage language) =>
        T(language, "ui.json path", "ui.json 路径");

    public static string KlHome(UiLanguage language) =>
        T(language, "KL_HOME", "KL_HOME");

    public static string ReadOnly(UiLanguage language) =>
        T(language, "read-only", "只读");

    public static string SkillsTitle(UiLanguage language) =>
        T(language, "Skills", "Skills");

    public static string SkillsEmpty(UiLanguage language) =>
        T(
            language,
            "  No skills found. Add SKILL.md under .kl/skills or ~/.kl/skills.",
            "  暂无 Skills。请在 .kl/skills 或 ~/.kl/skills 下添加 SKILL.md。");

    public static string SkillsError(UiLanguage language, string message) =>
        T(language, "Skills unavailable: " + message, "Skills 不可用：" + message);

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

    public static string AgentMenuFooter(UiLanguage language) =>
        T(
            language,
            "↑/↓ move  Enter open  Tab pane  Esc back  F2 save & exit",
            "↑/↓ 移动  Enter 打开  Tab 切换  Esc 返回  F2 保存并退出");

    public static string AgentEditFooter(UiLanguage language) =>
        T(
            language,
            "↑/↓ select  Enter confirm  Esc cancel/back  F2 save & exit",
            "↑/↓ 选择  Enter 确认  Esc 取消/返回  F2 保存并退出");

    public static string ModelPickerFooter(UiLanguage language) =>
        T(
            language,
            "↑/↓ select  Enter confirm  Esc cancel/back  F2 save & exit",
            "↑/↓ 选择  Enter 确认  Esc 取消/返回  F2 保存并退出");

    public static string ConfigFooter(UiLanguage language) =>
        T(
            language,
            "Enter/Space toggle language  Tab pane  Esc back  F2 save & exit",
            "Enter/空格 切换语言  Tab 切换  Esc 返回  F2 保存并退出");

}
