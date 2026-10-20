# CC-Switch CLI 设置页与供应商页参考

> 参考对象：`SaladDay/cc-switch-cli`  
> 用途：为 KL 的 CMD/TUI 设置页提供布局与交互参考  
> 日期：2026-10-18  
> 注意：`cc-switch-cli` 不是 Agent，本项目只参考它的终端 UI 组织方式，不复制其业务模型。

---

## 1. 参考项目概览

`cc-switch-cli` 是一个 Rust 编写的跨平台 CLI/TUI 工具，用于管理 Claude Code、Codex、Gemini、OpenCode 等 AI 编程工具的供应商配置、MCP、Skills、Prompts、会话和代理状态。

它的 TUI 使用：

- **Rust**
- **ratatui**
- **crossterm**

主要代码位置：

- `src-tauri/src/tui/tui/mod.rs`
- `src-tauri/src/tui/tui/ui.rs`
- `src-tauri/src/tui/tui/ui/providers.rs`
- `src-tauri/src/tui/tui/form.rs`
- `src-tauri/src/tui/tui/route.rs`
- `src-tauri/src/tui/tui/help.rs`
- `src-tauri/src/provider.rs`

参考链接：

- <https://github.com/SaladDay/cc-switch-cli>
- <https://github.com/SaladDay/cc-switch-tui/blob/main/CLAUDE.md>
- <https://github.com/SaladDay/cc-switch-tui/blob/main/src-tauri/src/tui/tui/ui.rs>
- <https://github.com/SaladDay/cc-switch-tui/blob/main/src-tauri/src/tui/tui/ui/providers.rs>
- <https://github.com/SaladDay/cc-switch-tui/blob/main/src-tauri/src/tui/tui/form.rs>
- <https://github.com/SaladDay/cc-switch-tui/blob/main/src-tauri/src/provider.rs>

---

## 2. 整体终端布局

`ui.rs` 的主渲染结构很清晰：

```text
┌─────────────────────────────────────────────┐
│ Header                                      │  固定 3 行
├──────────────┬──────────────────────────────┤
│              │                              │
│ Left Nav     │ Content                      │  中间自适应
│ 固定宽度      │ 最小宽度，占满剩余空间          │
│              │                              │
├──────────────┴──────────────────────────────┤
│ Footer / Key Bar                            │  固定 1 行
└─────────────────────────────────────────────┘
```

关键点：

1. **垂直三段式**
   - Header：产品名、当前 App、当前 Provider/Model、状态。
   - Body：左侧导航 + 右侧内容。
   - Footer：快捷键提示、当前焦点、状态。

2. **中间左右两栏**
   - 左栏固定宽度，只放导航分组。
   - 右栏占满剩余空间，只放当前分组内容。
   - 不在主界面塞大段说明文字。

3. **Overlay / Toast**
   - 表单、确认框、帮助、Toast 显示在内容层之上。
   - Toast 不长期占据主布局。

4. **过滤栏**
   - Provider、MCP、Skills 等列表页可在内容区顶部显示过滤栏。
   - 过滤栏只在需要时出现，不常驻。

---

## 3. 左侧导航

`route.rs` 中的导航项大致包括：

```text
Main
Providers
Mcp
Skills
Sessions
Prompts
Usage
Config
Settings
Exit
```

特点：

- 导航是“功能分组”，不是“表单项”。
- 供应商、MCP、Skills、会话、用量、配置、设置各自独立。
- 设置页与配置页分开：
  - `Config` 偏应用配置；
  - `Settings` 偏全局设置、代理、账号等。
- 当前项用高亮背景或标记表示。
- 左栏宽度有限，不显示长说明。

KL 建议映射：

```text
Model
Agent
Plugins
Skills
```

第一版只完整实现 `Model`，其余先占位。

---

## 4. 供应商页布局

`ui/providers.rs` 的供应商页是**列表页**，不是一次性把所有字段摊开。

### 4.1 页面结构

```text
┌ Providers ─────────────────────────────────────┐
│ [Filter / Search]                              │
│                                                │
│  ●  Provider Name        https://api...        │
│  ○  Provider Name        https://api...        │
│  ○  Provider Name        https://api...        │
│                                                │
├────────────────────────────────────────────────┤
│ Key bar: Enter edit  n new  d delete  ? help   │
└────────────────────────────────────────────────┘
```

供应商表格至少包含：

- 当前/选中标记；
- Provider 名称；
- API URL；
- 可选状态徽标；
- 可选配额/健康信息。

第一版 KL 不需要复制所有列，建议：

```text
State | Provider | Protocol | Endpoint | Models
```

### 4.2 列表页职责

列表页只负责：

- 浏览；
- 过滤；
- 选择；
- 切换当前 Provider；
- 打开新增/编辑表单；
- 显示 enabled、key_configured 等状态。

不在列表页放完整配置表单。

### 4.3 空状态和加载状态

参考项目区分：

- 加载中；
- 空列表；
- 有数据。

KL 也应区分，避免把“加载中”显示成“没有 Provider”。

### 4.4 选中态

- 当前 Provider 用独立标记；
- 光标选中行用高亮；
- 不要只靠颜色区分，至少同时使用 `>`、`●`、`*` 等字符。

---

## 5. 新增/编辑供应商表单

`cc-switch-cli` 的 Provider 表单是一个独立表单状态，包含：

- 模板/预设；
- 字段列表；
- 文本编辑状态；
- 内联字段错误；
- 未保存变更检测；
- 提交/取消；
- JSON 预览；
- 分页/子页面。

### 5.1 表单字段组织

参考项目的 Provider 表单按逻辑分组：

```text
Template
Basic
  ID
  Name
  Website
  Notes
API
  Base URL
  API Format
  API Key
  Model
Advanced
  Headers
  Request overrides
  Common config
```

KL 第一版 Model 分组建议：

```text
Provider
  ID
  Enabled
  API Key
  Endpoint mode: base / full

Endpoints
  base mode:
    Base URL
    computed openai-chat URL
    computed anthropic URL
    computed openai-responses URL
  full mode:
    openai-chat URL
    anthropic URL
    openai-responses URL

Models
  Model ID
  Protocol
  Context window
  Compact threshold
  Max output tokens
  Default model
```

### 5.2 表单不要直接塞进列表

推荐：

- 列表页按 Enter 打开表单；
- 表单覆盖内容区或作为 Overlay；
- Esc 取消；
- Ctrl+S 保存；
- 有未保存修改时，Esc 需要确认；
- 保存失败保留输入并显示字段错误。

### 5.3 内联校验

参考项目有 `InlineFieldError` 思路。KL 应采用同样原则：

- 错误显示在对应字段下方；
- 不弹一个长错误页；
- URL、Key、上下文窗口、阈值都做就地校验；
- 校验失败时焦点跳到第一个错误字段。

### 5.4 表单帮助

`CLAUDE.md` 明确要求：

- 主界面只显示字段、当前值和可执行动作；
- 行为说明、校验规则、长提示放在当前字段的 `?` 帮助里；
- 不要增加长期占空间的说明面板。

KL 应照此执行：

```text
Footer: Enter edit  Ctrl+S save  Esc cancel  ? help
```

按 `?` 时显示当前焦点字段的说明，而不是常驻帮助区。

---

## 6. 请求地址模式：base 与 full

`cc-switch-cli` 的 `ProviderMeta.isFullUrl` 语义很明确：

> `isFullUrl = true` 时，`base_url` 被视为完整 API 端点，不再拼接 endpoint 路径。

这与 KL 的需求一致，但 KL 在 UI 上再提供一层更方便的“基础地址模式”。

### 6.1 base 模式

默认模式。

用户输入：

```text
https://opencode.ai/zen/go/v1
```

KL 自动生成：

```text
openai-chat       -> https://opencode.ai/zen/go/v1/chat/completions
anthropic         -> https://opencode.ai/zen/go/v1/messages
openai-responses  -> https://opencode.ai/zen/go/v1/responses
```

规则：

- 默认值为 `https://opencode.ai/zen/go/v1`；
- 末尾斜杠归一化，避免 `//`；
- 生成的地址只在 UI 层计算；
- 最终写入现有 provider TOML 的三个完整 URL 字段；
- Core 继续把 URL 当作完整地址原样使用；
- Core 不新增路径拼接逻辑；
- 不根据模型名猜 URL。

### 6.2 full 模式

用户分别输入：

```text
openai-chat URL
anthropic URL
openai-responses URL
```

规则：

- 三个地址完全独立；
- 不自动拼接；
- 不改写用户输入；
- 只校验是合法 `http/https` URL；
- 某个 model 使用的 protocol 必须有对应 URL。

### 6.3 模式切换

- 默认 `base`；
- 切到 `full` 时，把当前计算出的三个 URL 作为默认值填入三个输入框；
- 从 `full` 切回 `base` 时，不猜测哪个 URL 是 base，保留原 full 值并提示用户确认；
- 保存前在摘要区显示最终将写入的三条 URL。

---

## 7. 供应商模型列表

参考项目的 Provider 表单包含模型相关字段和模型目录。KL 第一版只做最小闭环：

```text
Model ID
Protocol
Context window
Compact threshold
Max output tokens
Enabled / default
```

协议只允许：

- `openai-chat`
- `anthropic`
- `openai-responses`

模型列表显示：

```text
* gpt-5-responses   openai-responses   200k   80%   8k
  claude-sonnet     anthropic          200k   80%   8k
  gpt-5-chat        openai-chat        200k   80%   8k
```

`*` 表示默认模型。

---

## 8. 密钥处理

参考项目的 CHANGELOG 提到，它在新增/编辑表单中曾直接显示 API Key，只在详情页掩码。KL 不应照搬这一点。

KL 规则：

- 表单中始终掩码；
- 只显示 `key_configured: yes/no`；
- 支持 `R` 或 Enter 替换 Key；
- 保存后不回显明文；
- `config.get`、`provider.list`、日志、审计、错误信息都不得包含 Key；
- Key 写入 `~/.kl/credentials/<id>.key`；
- Provider TOML 只保存 `api_key_env` 或 `api_key_file`。

---

## 9. 页面状态与交互

### 9.1 页面状态

```text
Loading
Empty
Ready
Filtered
Editing
Saving
Error
```

### 9.2 快捷键

建议 KL 设置页：

```text
↑/↓        移动当前列表/表单字段
←/→        可选：切换左栏/右栏
Tab        切换左栏/右栏
Shift+Tab  反向切换
Enter      打开/编辑/确认
Esc        返回/取消
Ctrl+S     保存
?          当前焦点帮助
```

### 9.3 未保存修改

- 修改字段后标记 dirty；
- Esc 返回时弹出确认；
- 保存成功后清理 dirty；
- 保存失败保留表单和错误。

### 9.4 Resize

- 主布局按终端宽高重算；
- 左栏固定宽度，右栏自适应；
- 窄终端时 Provider 列表和详情改为上下布局；
- 长 URL 截断显示，编辑时完整显示；
- 不因为 resize 丢失焦点和输入。

---

## 10. 错误、空状态和加载状态

### 空状态

```text
No providers configured
Press n to add a provider
```

### 加载状态

```text
Loading providers...
```

### 错误状态

```text
Failed to load providers
Reason: ...
Press r to retry
```

### 保存错误

- 字段级错误就地显示；
- 顶部只显示简短失败摘要；
- 不关闭表单。

---

## 11. 视觉层级

参考项目使用：

- Header 边框；
- 左栏独立背景/选中高亮；
- 内容区留白；
- Footer 固定快捷键；
- 表格行高亮；
- 状态徽标；
- 无颜色模式。

KL 建议：

- Header：标题 + 当前 Provider + enabled 状态；
- 左栏：分组名，选中项高亮；
- 右栏：列表/表单；
- Footer：快捷键；
- 不使用过多颜色，保证 `NO_COLOR` 可用；
- 选中、错误、成功同时有文字或符号，不只靠颜色。

---

## 12. KL 第一版 Model 分组建议布局

```text
┌ KL Settings ────────────────────────────────────────────────┐
│ Model        │ Providers                                   │
│ Agent        │ ┌─────────────────────────────────────────┐ │
│ Plugins      │ │ > opencode    enabled   key: yes        │ │
│ Skills       │ │   example     disabled  key: no         │ │
│              │ └─────────────────────────────────────────┘ │
│              │                                             │
│              │ Selected: opencode                          │
│              │ Mode: base                                  │
│              │ Base: https://opencode.ai/zen/go/v1         │
│              │                                             │
│              │ Models                                      │
│              │ * gpt-5-responses  openai-responses  200k   │
│              │   claude-sonnet    anthropic         200k   │
│              │   gpt-5-chat       openai-chat       200k   │
├──────────────┴─────────────────────────────────────────────┤
│ ↑/↓ move  Tab pane  Enter edit  Ctrl+S save  Esc back  ?   │
└────────────────────────────────────────────────────────────┘
```

### 新增/编辑表单布局

```text
┌ Edit Provider: opencode ────────────────────────────────────┐
│ ID              [opencode____________________]             │
│ Enabled         [x]                                         │
│ API Key         [********]  key configured                  │
│ Endpoint mode   (base) (full)                               │
│                                                             │
│ Base URL        [https://opencode.ai/zen/go/v1____]         │
│ openai-chat     https://.../chat/completions  (computed)   │
│ anthropic       https://.../messages          (computed)   │
│ responses       https://.../responses         (computed)   │
│                                                             │
│ Models                                                      │
│ > gpt-5-responses   openai-responses   200000   0.80   8192 │
│   claude-sonnet     anthropic          200000   0.80   8192 │
│                                                             │
│ [Save] [Cancel]                         ? field help        │
└─────────────────────────────────────────────────────────────┘
```

---

## 13. 不建议照搬的部分

第一版不要复制：

- 参考项目的全部 Provider 字段；
- 多 App 切换、代理、故障转移、配额、会话、WebDAV、S3；
- Provider 模板市场；
- JSON 预览和复杂请求覆盖；
- 多级子页面；
- 新增/编辑表单中显示明文 API Key；
- 常驻说明面板。

这些可以后续按真实需求增加。

---

## 14. KL 实现映射

建议只改 C# CLI 与测试：

- `tui/Kl.Tui/`：设置页状态、渲染、输入处理、表单；
- `tui/Kl.Tui.Tests/`：布局、URL 展开、字段校验、快捷键、resize；
- 复用现有 Core 方法：
  - `provider.list`
  - `provider.upsert`
  - `provider.set_api_key`
  - `provider.set_enabled`
  - `config.set_default_model`

不要在 C# 重复实现 TOML 解析/写盘，不要把 base URL 拼接逻辑放进 Rust Core。

---

## 15. 验收清单

### 布局

- [ ] `kl` 默认进入聊天页；
- [ ] 输入 `/settings` 或 `/设置` 进入设置页；
- [ ] 设置页为左导航 + 右内容；
- [ ] 左栏有 Model、Agent、Plugins、Skills；
- [ ] Model 分组可浏览 Provider 列表；
- [ ] Footer 显示快捷键；
- [ ] resize 后焦点和输入不丢失；
- [ ] 非交互输出回退纯文本。

### Model 分组

- [ ] 显示 provider、enabled、key_configured、model 数量；
- [ ] 新增/编辑 Provider；
- [ ] Key 掩码输入；
- [ ] base 模式默认 `https://opencode.ai/zen/go/v1`；
- [ ] base 模式正确生成三条完整 URL；
- [ ] full 模式保留用户输入；
- [ ] 三个 URL 原样写入 Provider TOML；
- [ ] 模型列表显示 protocol、context_window、compact_threshold、max_output_tokens；
- [ ] 可选择默认 Provider/Model；
- [ ] enabled=false 时模型不显示；
- [ ] 保存失败保留输入并显示字段错误。

### 安全

- [ ] Key 不出现在 UI、日志、审计、错误信息；
- [ ] Key 不写入 Provider TOML；
- [ ] URL 只接受 http/https；
- [ ] 字段校验在保存前执行。

---

## 16. 结论

`cc-switch-cli` 值得学习的不是它的业务范围，而是它的 TUI 结构：

1. 固定 Header + 左导航 + 右内容 + Footer；
2. 列表页和编辑表单分离；
3. 主界面只放字段、状态和动作；
4. 长说明放到 `?` 上下文帮助；
5. 内联字段错误；
6. 未保存修改确认；
7. 加载、空、错误状态明确区分；
8. 表格列简洁，详细信息进入编辑页。

KL 第一版只需把这些原则落到 Model 分组，不复制它的全部功能。
