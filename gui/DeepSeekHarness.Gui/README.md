# DeepSeek Harness GUI

Avalonia 桌面客户端，界面与交互对照 DeepSeek Harness 0.2.1-rc.1 实机界面和源码实现。
项目独立于原始 React/Electron checkout，不会修改 D:\kl\demo\deepseek-harness。

## 界面

- 280px 侧栏、工作区列表、会话搜索、Codex 风格 Chat/Work 模式菜单
- 内嵌圆角主面板，空会话居中输入，开始会话后输入框停靠底部
- 用户气泡、Markdown 助手正文、代码块、复制操作、折叠思考和工具调用
- DeepSeek 风格设置模态：通用设置、模型、本地数据、关于与快捷键
- 插件页、自动化任务页与实机 DeepSeek Harness 对齐的标题、筛选和列表结构

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

    dotnet run --project .\DeepSeekHarness.Gui.csproj

状态文件默认位于：

    %LOCALAPPDATA%\DeepSeekHarnessGui\state.json

环境变量覆盖：

- DEEPSEEK_HARNESS_GUI_STATE：状态文件路径
- DEEPSEEK_HARNESS_WORKSPACE：首次启动工作区
- DEEPSEEK_HARNESS_GUI_PLUGINS：本地插件目录

## 测试

    dotnet test ..\DeepSeekHarness.Gui.Tests\DeepSeekHarness.Gui.Tests.csproj

## 发布

    E:\kl\build-deepseek-gui.bat
    E:\kl\run-latest.bat

构建脚本先运行 Release 测试，再发布到新的 E:\kl\deepseek-gui-yyyyMMdd-HHmmss 目录并更新启动脚本。

