# KL 热更新计划

> 状态：草案，2026-11-25
>
> 目标：参考 Unity 的 HybridCLR + YooAsset 思路，为 KL 做按文件增量更新。
> 说明：HybridCLR / YooAsset 依赖 Unity 的 IL2CPP 与 AssetBundle，不能直接用于
> .NET / Avalonia / Rust 技术栈；这里只借鉴它们的设计思路。

## 1. 结论

- **按文件增量下载：可以实现。** 这是 YooAsset 的核心价值，适合 KL。
- **运行中替换主程序 C# 代码：做不到。** Windows 会锁定正在运行的
  `kl.exe` / `kl.dll`；.NET 没有 HybridCLR 等价物。
- **`kl-core` 可以进程级热重启。** GUI 不退出，停掉 core、替换文件、
  重启 core，客户端自动重连并 resume。
- **本地化、主题、图片、字体、prompt、skills 可以运行中热加载。**
  前提是把它们从嵌入资源改成外部文件，加 `FileSystemWatcher`。
- **插件程序集可以热重载。** 用 `AssemblyLoadContext` 卸载旧 DLL、加载新
  DLL；主程序集本身仍不能替换。

## 2. 能力矩阵

| 内容 | 不重启更新 | 说明 |
|---|---|---|
| `kl-core.exe` / `kl-sandbox-runner.exe` | 可以（进程级） | 停 core → 替换 → 重启 → 重连会话 |
| `gui/kl.exe`、`gui/kl.dll` | 不行 | 下载后提示重启 |
| `tui/kl.exe`、`tui/kl.dll` | 不行 | 下载后提示重启 |
| Avalonia 编译进 DLL 的 UI | 不行 | 换 UI 必须换 DLL + 重启 |
| `catalog.json` 本地化 | 可以 | 需改为磁盘优先、嵌入兜底 |
| 主题 / 图片 / 字体 | 可以 | 需改为外部资源 |
| 插件 DLL | 可以 | `AssemblyLoadContext` 热重载 |
| 会话 / 配置 / 数据 | 不需要 | 已在 `~/.kl`，与安装目录分离 |

## 3. 推荐架构：版本目录 + 对象存储

不要原地覆盖正在运行的文件。使用版本目录 + 独立 updater：

```text
安装目录/
├── current/                 ← 当前版本
│   ├── gui/
│   ├── tui/
│   └── manifest.json        ← 本地已安装文件清单
├── versions/
│   └── 0.2.0/               ← 新版本暂存
├── objects/
│   └── ab/cd/abcdef...      ← 按 SHA-256 存放的下载对象
├── updates/
├── kl-updater.exe           ← 独立更新器
└── kl-updater.cmd
```

## 4. 远程清单

```json
{
  "version": "0.2.0",
  "channel": "stable",
  "protocol": 1,
  "published_at": "2026-11-25T12:00:00Z",
  "files": [
    {
      "path": "gui/kl.dll",
      "size": 1234567,
      "sha256": "....",
      "url": "https://example.com/objects/ab/cd/abcdef..."
    },
    {
      "path": "gui/Avalonia.Base.dll",
      "size": 123456,
      "sha256": "....",
      "url": "https://example.com/objects/ab/cd/..."
    }
  ],
  "deleted": ["gui/old.dll"],
  "signature": "base64-ed25519-signature"
}
```

- `path` 相对 `current/`。
- `sha256` 同时用于内容校验和对象去重。
- `deleted` 列出新版本中不再需要的旧文件。
- `signature` 对清单正文签名，客户端内置公钥。

## 5. 更新流程

1. 拉取并**验签** `manifest.json`。
2. 对比本地文件哈希，只下载缺失或哈希不一致的文件。
3. 下载对象按 SHA-256 校验，写入 `objects/`。
4. 组装新版本到 `versions/<version>/`，不触碰 `current/`。
5. 所有文件就绪后提示重启：
   - 有正在跑的 turn：先完成或取消。
   - core 更新可以单独重启；GUI/TUI 更新需要退出应用。
6. `kl-updater.exe` 在所有 KL 进程退出后：
   - 等待进程退出
   - `current` → `current.old`
   - `versions/<version>` → `current`
   - 启动新版本并健康检查
   - 失败自动回滚 `current.old`
7. 成功后清理旧版本和不再被引用的对象。

## 6. 代码更新的三条路

### 6.1 普通 C# / Rust 代码

下载新 DLL/EXE → 提示重启。GUI 重启后通过 `session.resume` / `attach`
恢复会话；core 重启期间客户端显示重连状态。

### 6.2 可热重载的插件

把需要频繁更新的功能拆成插件程序集：

- 主程序只保留壳、协议、基础 UI。
- 功能模块通过 `AssemblyLoadContext` 加载。
- 更新时卸载旧 ALC、加载新 DLL。
- 跨 ALC 的类型必须接口化；插件不能修改主程序集里的类型。

### 6.3 外部化资源

优先把以下内容改成外部文件：

- `gui/Kl.Gui/Resources/i18n/catalog.json`
- 主题 / 图片 / 字体
- `AGENTS.md`、skills、prompt 片段

用 `FileSystemWatcher` 监听后重新加载，不需要重启。

## 7. 需要新增的模块

1. `scripts/update_manifest.py`
   - 遍历发布目录，生成 `manifest.json`
   - 计算 SHA-256、按对象去重
   - 用 Ed25519 签名
2. `sdk/Kl.Client/UpdateService.cs`
   - 请求清单、验签
   - 对比本地哈希
   - 下载、校验、暂存
3. `kl-updater`（Rust 小工具或 C# 控制台）
   - 等进程退出
   - 原子替换目录
   - 回滚
   - 重启
4. GUI
   - 设置页“检查更新”
   - 启动时低频检查（例如 24 小时一次）
   - “重启以更新”提示
5. TUI
   - `kl update` 命令，复用同一套 UpdateService / updater
6. Release workflow
   - 手动触发时构建 zip
   - 生成 manifest
   - 签名
   - 上传 GitHub Releases 或静态 HTTPS

## 8. 分阶段实施

1. **文件级增量更新器**：manifest + 哈希对比 + updater + 重启提示。
2. **外部化本地化 / 资源**：运行中热加载。
3. **core 独立热重启**：GUI 不退出，自动重连。
4. **插件 ALC**：实现部分代码热重载。
5. **增量压缩 / CDN / 灰度通道**：zstd、bsdiff、分块 Range。
6. **签名与发布自动化**：Ed25519、Authenticode、release 工作流。

## 9. 安全要求

- 清单必须验签；客户端内置公钥，拒绝未签名或签名错误。
- 下载对象按 SHA-256 校验后才允许替换。
- 只允许 HTTPS 更新源。
- Windows 二进制做 Authenticode 签名，减少 SmartScreen 拦截。
- 更新失败必须回滚，不能留下半个版本。
- 不触碰 `~/.kl` 下的会话、配置、凭据。

## 10. 验收标准

- 只下载有变化的文件；未变化文件不重复下载。
- 下载中断可续传，校验失败不落盘。
- 替换失败自动回滚，旧版本可继续启动。
- 有进行中的 turn 时，更新不会强杀会话。
- core 更新期间 GUI 显示重连，恢复后历史完整。
- 协议版本不兼容时拒绝更新，并给出提示。
- 安装目录之外不写任何文件（日志/缓存除外）。

## 11. 待确认

- 更新源：GitHub Releases，还是自己的 HTTPS 地址？
- 签名方式：Ed25519 自签名，还是代码签名证书？
- 安装目录：`%LOCALAPPDATA%\Programs\KL`，还是用户自选？
- 是否先只做 Windows，再扩到 macOS/Linux？
- 是否需要灰度通道（stable / beta）？
