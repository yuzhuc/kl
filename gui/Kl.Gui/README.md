# kl

Avalonia 桌面客户端，采用 kl 品牌和 Codex 风格界面。
项目为独立 Avalonia 实现，不依赖原始外部 checkout。

## 界面

- 56px 图标导航栏 + 280px 项目/会话栏 + 自适应对话区，三栏结构与 Codex 对齐
- 内嵌圆角主面板，空会话居中输入，开始会话后输入框停靠底部
- 用户气泡、Markdown 助手正文、代码块、复制操作、折叠思考和工具调用
- kl 设置模态：通用设置、模型、本地数据、关于与快捷键
- 插件页、自动化任务页采用统一的 kl 深色视觉和列表结构

## 功能

- OpenAI 兼容 chat/completions SSE 流式聊天
- reasoning_content / reasoning 增量思考展示
- 工具调用参数和状态展示
- 文本与代码附件进入模型请求，并随会话状态持久化
- 新建、切换、搜索、重命名、删除会话
- 空会话不写入缓存，发送首条消息后才持久化
- 工作区切换与目录选择
- Chat / Work 模式切换并恢复上次选择
- 内容字号设置
- Endpoint、模型、API Key、权限模式
- 连接测试、恢复默认值、状态目录打开与路径复制
- 本地插件目录扫描和插件文件夹导入
- 自动化任务创建、编辑、启停、立即运行、间隔调度和结果持久化
- 请求错误、停止生成、持久化失败提示
- 快捷键：Ctrl+N、Ctrl+Enter、Ctrl+F、Ctrl+,

## 配置

API Key 在设置页面直接填写，并保存在本机状态文件中。

    dotnet run --project .\Kl.Gui.csproj

GUI 数据统一位于 `%USERPROFILE%\.kl\gui`：

- 状态文件：`%USERPROFILE%\.kl\gui\state.json`
- API Key：`%USERPROFILE%\.kl\credentials\deepseek.key`
- 插件目录：`%USERPROFILE%\.kl\plugins`
- 旧版设置：`%USERPROFILE%\.kl\gui\gui-settings.json`

旧版 `%LOCALAPPDATA%\DeepSeekHarnessGui` 和
`%LOCALAPPDATA%\Kl\gui-settings.json` 会在首次启动时自动迁移，随后删除旧目录。

环境变量覆盖：

- KL_GUI_STATE：状态文件路径
- KL_GUI_WORKSPACE：首次启动工作区
- KL_GUI_PLUGINS：本地插件目录

## 测试

    dotnet test ..\Kl.Gui.Tests\Kl.Gui.Tests.csproj

## 发布

在仓库根目录运行：

```powershell
python scripts/build.py
```

最终 GUI 发布到 `D:\kl\bin\gui`，启动脚本为 `D:\kl\bin\run-gui.bat`。

