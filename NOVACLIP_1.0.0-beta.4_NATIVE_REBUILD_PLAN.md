# NovaClip 1.0.0-beta.4 — Native Windows Rebuild 项目规划

> **文档性质：强制执行的下一测试版主规划**
>
> 目标版本：`1.0.0-beta.4`
>
> 代号：`Native Windows Rebuild`
>
> 本文档专门用于 **彻底重构 NovaClip 当前测试版**。它不是对 beta.3 做表面修补，而是重新建立应用外壳、浏览器体验、下载探测、设置系统、本地化、扩展接口和 Windows 11 设计体系。
>
> **从本版本开始，任何“能编译就算完成”“进程不退出就算能启动”“先把按钮堆上去以后再整理”的实现方式都不再接受。**
>
> 当前 `PROJECT_PLAN.md` 可继续作为早期业务逻辑/下载行为参考，但对于 **UI、应用结构、浏览器、设置、本地化、扩展架构、Windows 11 设计规范和验收标准**，以本文档为最高优先级。

---

# 0. Codex 执行总命令

Codex 获取本文件后应直接开始，不再向用户询问技术路线，不允许以权限、macOS 环境或 Windows 专属 API 为理由中断。

执行时必须遵守：

1. 从当前 `main` 创建开发分支：
   - `refactor/beta.4-native-rebuild`
2. 把当前 UI 层视为 **Legacy UI**，不得继续在现有混乱结构上叠补丁。
3. 可复用已经有自动化测试证明正确的：
   - 下载核心
   - SQLite
   - Range 下载
   - Retry
   - PlayURL normalizer
   - 更新器中的可复用逻辑
4. 必须重新建立：
   - WinUI 3 App Shell
   - MainWindow
   - TitleBar
   - Navigation
   - Browser host
   - Browser navigation policy
   - Media detection coordinator
   - Settings UI
   - Localization resources
   - Windows-specific service composition
5. Windows 专属编译和真实启动验证全部通过 GitHub Actions Windows Runner 完成。
6. 每次 CI 失败都必须读取日志并继续修复。
7. **不得在 CI 没有验证真实 UI 成功加载时发布 beta.4。**
8. beta.4 Release 必须在所有 P0 验收项完成后才能创建。

---

# 1. 为什么 beta.4 必须重构

当前 beta.1～beta.3 的最大问题并不是某一个单点 Bug，而是架构和产品体验不够稳定：

- App 外壳并没有形成一个完整、统一的 Windows 11 原生体验。
- UI 逻辑、页面、服务初始化和 XAML 生命周期耦合过重。
- 浏览器只是“嵌进去一个 WebView2”，没有被做成完整浏览体验。
- `target="_blank"` / `window.open()` 会产生额外窗口。
- 地址栏、导航状态、加载状态、外链策略不完整。
- 媒体探测器缺乏稳定的事件协调机制。
- 设置项只是“把配置字段直接暴露给用户”，而不是做真正的设置 UX。
- 存在大量用户可见字符串硬编码。
- API / service boundary 不够清晰，继续扩展会越来越乱。
- CI 过去曾出现“进程活着 = 启动成功”的假阳性。
- 当前代码内部仍保留大量历史项目名 `BiliNative`，与 NovaClip 品牌和未来架构不一致。

因此 beta.4 定义为：

> **在保留可复用核心能力的前提下，重新构建 NovaClip 的原生 Windows 应用层和产品架构。**

---

# 2. beta.4 的六项不可妥协目标

## 2.1 真正的 Windows 11 原生应用

必须使用：

```text
C#
.NET 10
WinUI 3
Windows App SDK
WebView2（仅作为网页浏览区域）
Windows App SDK Windowing / AppWindow
Windows 原生文件选择器、通知、主题、标题栏和生命周期 API
```

明确禁止：

- Electron
- Tauri
- Flutter
- Avalonia
- React Native for Windows
- HTML/CSS 作为 App Shell
- 把整个应用做成 WebView 套壳
- 自绘一套和 Windows 11 不一致的“类 Fluent UI”

**WebView2 只能负责 Bilibili 网页。**

应用的：

- 标题栏
- 主导航
- 设置
- 下载列表
- 历史
- Dialog
- InfoBar
- 菜单
- 进度
- 文件选择
- 更新
- 错误提示

必须全部是 WinUI 3 / Windows 原生能力。

---

## 2.2 浏览器必须从“能显示网页”升级为“能正常使用”

beta.4 浏览器至少必须做到：

- 单窗口。
- 默认不弹出额外 WebView2 窗口。
- Bilibili 内部链接始终在 App 内打开。
- 支持后退。
- 支持前进。
- 支持刷新 / 停止。
- 支持主页。
- 地址栏实时跟随 URL。
- 页面标题跟随网页更新。
- 正确处理 SPA 导航。
- 显示加载状态。
- WebView2 崩溃时给出恢复按钮。
- 外部非 Bilibili 链接有清晰策略。
- 浏览历史栈和按钮状态正确。
- 登录状态持久化。
- 禁止 `window.open()` 自己生成失控窗口。

核心必须实现：

```csharp
CoreWebView2.NewWindowRequested
CoreWebView2.NavigationStarting
CoreWebView2.NavigationCompleted
CoreWebView2.SourceChanged
CoreWebView2.HistoryChanged
CoreWebView2.DocumentTitleChanged
CoreWebView2.ProcessFailed
```

### NewWindowRequested 强制行为

Bilibili 页面触发：

```text
target="_blank"
window.open()
新标签页链接
```

时：

```text
如果是 http/https 且属于允许的 Bilibili 导航
→ args.Handled = true
→ 当前 WebView Navigate(args.Uri)

如果是外部站点
→ 不允许 WebView2 自动弹窗
→ 交给 IBrowserNavigationPolicy
→ 默认显示非阻塞提示并允许“使用系统浏览器打开”
```

beta.4 暂不实现多标签页，但必须预留：

```csharp
IBrowserTabService
IBrowserTab
```

接口，以便未来增加标签页时不重写 BrowserPage。

---

## 2.3 下载探测必须重新设计

当前“监听到一点 PlayURL 就塞进 UI”的方式不足以长期维护。

beta.4 必须引入：

```text
Media Detection Pipeline
```

### 探测状态

```text
Idle
WaitingForPageContext
Observing
CandidateFound
Resolving
Ready
Unsupported
PermissionDenied
Expired
Error
```

UI 必须明确展示当前状态，而不是只有一个莫名其妙的“添加到下载”按钮。

### 探测来源

必须设计为多个可替换策略：

```csharp
IPageContextSource
IPlayUrlObservationSource
IBilibiliApiResolver
IHydrateDataResolver
IMediaDetectionCoordinator
```

默认策略优先级：

1. 当前 WebView 页面上下文。
2. WebView2 实际播放器 `/playurl` 网络响应。
3. 页面 hydrate / initial state。
4. 使用当前登录态进行 Bilibili resolver fallback。
5. 将来可以增加新的 resolver，而不改 UI。

### 探测器必须解决

- BV / AV。
- 多 P。
- Bangumi / Episode。
- Bilibili SPA 页面切换。
- 页面切换后旧 PlayURL 不能污染新页面。
- PlayURL 重复捕获要去重。
- URL 过期要能重新 resolve。
- CID 改变时重新探测。
- 登录态发生变化时重新探测。
- 只能将当前页面对应的媒体显示在 UI。
- 不允许不同 NavigationId 的响应串台。

### 每次探测结果必须有 fingerprint

例如：

```text
PageUrl
Bvid/Aid
Cid
EpisodeId
QualityId
Codec
NavigationGeneration
```

用来做：

- 去重
- 过期判断
- 重新解析
- Debug

---

# 3. 新解决方案结构

beta.4 不再继续扩大 `BiliNative.*` 命名。

目标逐步迁移为：

```text
NovaClip.sln

src/
  NovaClip.Contracts/
  NovaClip.Core/
  NovaClip.Bilibili/
  NovaClip.Infrastructure/
  NovaClip.Windows/
  NovaClip.App/
  NovaClip.Updater/

tests/
  NovaClip.Contracts.Tests/
  NovaClip.Core.Tests/
  NovaClip.Bilibili.Tests/
  NovaClip.Infrastructure.Tests/
  NovaClip.Windows.Tests/

docs/
  ARCHITECTURE.md
  API_SURFACE.md
  DESIGN_SYSTEM.md
  BROWSER.md
  MEDIA_DETECTION.md
  LOCALIZATION.md
  SETTINGS.md
  UPDATE.md
  PRIVACY.md
  SECURITY.md
  TESTING.md
  STATUS.md
```

## 3.1 `NovaClip.Contracts`

只放：

- 接口
- DTO
- Result 类型
- Event contracts
- Capability contracts

不得依赖：

- WinUI
- WebView2
- SQLite
- FFmpeg
- HTTP implementation

---

## 3.2 `NovaClip.Core`

负责：

- DownloadTask
- state machine
- Retry
- MediaDescriptor
- MediaTrack
- filename policy
- settings models
- update models
- versioning
- error catalog key
- command / result model

保持 `net10.0`。

---

## 3.3 `NovaClip.Bilibili`

Bilibili 专属适配层：

- page context parser
- PlayURL parser
- endpoint provider
- authenticated API client
- quality normalization
- codec normalization
- media detection strategies
- bangumi resolver
- durl/dash resolver

未来如果增加别的网站，不污染核心。

---

## 3.4 `NovaClip.Infrastructure`

负责：

- HttpRangeDownloader
- SQLite
- settings persistence
- task manifests
- downloads repository
- GitHub update feed
- file system
- retry implementation

---

## 3.5 `NovaClip.Windows`

所有 Windows-only adapter：

- WebView2 host
- Browser session
- Windows picker
- AppWindow
- TitleBar
- Mica
- Notifications
- FFmpeg process
- Windows launcher
- Clipboard
- App lifecycle
- Auto-start（未来）
- Tray（未来）

---

## 3.6 `NovaClip.App`

这里只负责：

- WinUI 3 View
- ViewModel
- navigation
- visual states
- localization binding
- command binding
- dependency injection composition root

UI 不直接做：

- HTTP
- SQLite
- Cookie parsing
- FFmpeg command assembly
- PlayURL JSON parsing

---

# 4. App Shell — 重新设计

主窗口采用标准 Windows 11 结构：

```text
┌───────────────────────────────────────────────────────┐
│ App Icon  NovaClip                        — □ ×       │
│───────────────────────────────────────────────────────│
│ NavigationView │                                   │
│                │                                   │
│  浏览          │          Current Page             │
│  下载          │                                   │
│  历史          │                                   │
│                │                                   │
│                │                                   │
│  ⚙ 设置        │                                   │
└───────────────────────────────────────────────────────┘
```

## 4.1 顶层导航只保留

```text
浏览
下载
历史
设置（NavigationView 原生 Settings 项）
```

“关于”不再占用顶层导航。

关于页进入：

```text
设置 → 关于 NovaClip
```

---

## 4.2 Windows 11 视觉要求

主窗口：

```text
MicaBackdrop
TitleBar
NavigationView
InfoBar
ContentDialog
CommandBar
ProgressBar
TeachingTip（只在真的需要引导时）
```

必须使用系统：

- Typography
- Accent color
- ThemeResource
- Corner radius
- Focus visuals
- High Contrast
- Light / Dark
- DPI scaling

禁止：

- 自己定义几十种品牌色。
- 每个东西都包一个 Card。
- 为了“好看”随意堆渐变。
- 与系统控件重复造轮子。
- 把按钮做成网页式超大胶囊。
- 到处使用意义不明的半透明块。

### 背景

主窗口：

```text
Mica
```

页面主 Grid 默认透明。

内容分组必要时使用 Windows 主题资源：

```text
CardBackgroundFillColorDefaultBrush
CardStrokeColorDefaultBrush
LayerFillColorDefaultBrush
```

---

# 5. 原生 TitleBar

采用 Windows App SDK 推荐 TitleBar / AppWindow 方案。

包含：

```text
NovaClip icon
NovaClip
可选页面标题
系统 Minimize
系统 Maximize/Restore
系统 Close
```

必须支持：

- Light
- Dark
- High Contrast
- Windows caption buttons
- DPI
- 正确 Drag Region

不得自己模拟窗口最小化/最大化/关闭按钮。

---

# 6. BrowserPage 全面重做

## 6.1 页面结构

```text
┌─────────────────────────────────────────────────────────┐
│ ←  →  ⟳   [ bilibili.com/...                     ]  ⌂ │
├─────────────────────────────────────────┬───────────────┤
│                                         │ 当前媒体      │
│                                         │               │
│                                         │ 标题          │
│              WebView2                   │ 分P / EP      │
│                                         │ 清晰度        │
│                                         │ 编码          │
│                                         │ 音频          │
│                                         │ 预计大小      │
│                                         │               │
│                                         │ [下载]        │
├─────────────────────────────────────────┴───────────────┤
│ Status / InfoBar                                       │
└─────────────────────────────────────────────────────────┘
```

右侧媒体面板：

- 默认宽度合理。
- 可折叠。
- 小窗口时切换到底部区域，而不是把 WebView2 挤到无法使用。
- 没探测到媒体时，不显示一大堆空字段。

---

## 6.2 Browser Toolbar

必须为真正浏览器行为：

### Back

```text
CanGoBack == false
→ Disabled
```

### Forward

```text
CanGoForward == false
→ Disabled
```

### Refresh / Stop

正在加载：

```text
Stop
```

空闲：

```text
Refresh
```

### AddressBox

支持：

```text
https://...
bilibili.com/...
BV...
av...
ep...
ss...
```

输入 `BV...` 时可由：

```csharp
IBilibiliUrlResolver
```

转换成完整 Bilibili URL。

### Home

固定行为由：

```csharp
IBrowserHomeService
```

提供。

默认：

```text
https://www.bilibili.com/
```

---

# 7. Browser Navigation Policy

定义：

```csharp
public interface IBrowserNavigationPolicy
{
    BrowserNavigationDecision Evaluate(Uri uri, BrowserNavigationKind kind);
}
```

决策：

```text
NavigateInCurrentView
OpenInSystemBrowser
Block
AskUser
```

### 默认规则

`bilibili.com` 与可信子域：

```text
NavigateInCurrentView
```

HTTP/HTTPS 外部域：

```text
AskUser / OpenInSystemBrowser
```

危险 Scheme：

```text
file:
javascript:
data:
vbscript:
未知自定义 scheme
```

默认：

```text
Block
```

特殊系统 scheme 必须逐项白名单。

---

# 8. WebView2 Session 架构

定义：

```csharp
public interface IBrowserSessionService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(Uri scope, CancellationToken cancellationToken);
    Task ClearSessionAsync(CancellationToken cancellationToken);
    string UserDataFolder { get; }
}
```

要求：

- NovaClip 独立 WebView2 profile。
- 不读取 Edge / Chrome profile。
- 不要求用户复制 Cookie。
- Cookie 不进入普通日志。
- Cookie 不写 SQLite。
- Cookie 不写 settings。
- Native downloader 需要时仅复制到内存。
- Clear Login Data 必须通过设置页提供清晰按钮。

---

# 9. 媒体探测 UI

当前媒体区域不能再只有：

```text
当前媒体
一些字段
下载按钮
```

必须把探测状态视觉化。

## 未检测

```text
当前页面未发现可下载媒体
播放视频后 NovaClip 会自动检测可用轨道
```

## 正在检测

```text
正在检测媒体…
ProgressRing
```

## 已检测

```text
标题
分P/EP
清晰度
视频编码
音频
预计大小
```

## 无权限

```text
InfoBar Severity=Warning
当前账号无法播放该清晰度或内容
```

## 探测失败

```text
InfoBar Severity=Error
重新检测
查看诊断信息
```

---

# 10. 清晰度和轨道选择重新设计

用户不能面对“80 / 112 / codec id”这种内部参数。

UI 显示：

```text
1080P
1080P 高码率
4K
HDR
Dolby Vision
```

如果需要显示技术参数，放在 Secondary Text：

```text
AV1 · 1920×1080 · 60 fps
```

### ComboBox item model

```csharp
MediaQualityOptionViewModel
{
    DisplayName
    SecondaryText
    QualityId
    Codec
    IsAvailable
    IsRecommended
}
```

---

# 11. SettingsPage 彻底重做

设置不是“数据库字段编辑器”。

必须遵守：

> 设置页只展示用户真正能理解、真正需要控制的选项。

并且 **能立即应用的设置立即保存，不再设置一个总“保存设置”按钮。**

---

# 12. 设置分类

```text
常规
下载
浏览器与登录
合并与 FFmpeg
更新
高级
关于 NovaClip
```

---

# 13. 设置控件选择规则

## Boolean

使用：

```text
ToggleSwitch
```

例如：

```text
自动检查更新
合并成功后删除临时文件
```

禁止：

```text
TextBox: true / false
```

---

## 1～5 个互斥选项

使用：

```text
RadioButtons
```

### 并发下载任务数

必须改成：

```text
○ 1
● 2  （推荐）
○ 3
```

禁止让用户自己输入数字。

---

## 多个明确枚举值

使用：

```text
ComboBox
```

例如：

```text
默认清晰度
编码偏好
日志等级
连接超时预设
```

---

## 大范围数字

只有真的需要任意数值时才使用：

```text
NumberBox
```

并设置：

```text
Minimum
Maximum
SmallChange
SpinButtonPlacementMode
```

---

## 文件夹

禁止 TextBox 让用户手输路径作为主要方式。

使用：

```text
只读路径
[选择文件夹]
[打开文件夹]
```

---

## 文件

FFmpeg：

```text
状态：已检测 / 未检测
路径：...
[选择 ffmpeg.exe]
[自动检测]
[测试]
```

---

# 14. beta.4 设置定义

## 常规

### Theme

```text
跟随系统
浅色
深色
```

默认：

```text
跟随系统
```

### App language

beta.4 可先：

```text
跟随系统
```

但 UI 和架构必须支持未来运行时语言选择。

---

## 下载

### 下载目录

Picker。

### 同时下载任务

RadioButtons：

```text
1
2（默认）
3
```

### 默认清晰度

ComboBox：

```text
最高可用
跟随播放器
1080P
720P
...
```

服务端不可用时安全降级并提示。

### 默认视频编码

```text
自动
AVC
HEVC
AV1
```

### 重试策略

不要暴露工程参数。

显示：

```text
标准
更积极
关闭自动重试
```

内部映射到 RetryPolicy。

---

## 浏览器与登录

### 启动时打开

```text
Bilibili 首页
上次页面
```

### Bilibili 外部链接

```text
使用系统浏览器打开
每次询问
```

### Login

显示：

```text
WebView2 数据状态
[清除登录数据]
```

清除必须确认。

---

## 合并与 FFmpeg

### 自动合并

Toggle。

### 删除临时轨道

Toggle。

### FFmpeg

状态卡：

```text
FFmpeg
✓ 已找到
C:\...
```

或者：

```text
FFmpeg
! 未找到
```

按钮：

```text
自动检测
选择文件
测试
```

---

## 更新

### 自动检查

Toggle。

### Channel

RadioButtons：

```text
Stable
Preview
```

### 当前版本

只读。

### 最新检查时间

只读。

### 操作

```text
[检查更新]
```

---

## 高级

只放：

- Debug logging
- Open log folder
- Export diagnostics
- Reset detector state
- Reset all settings

普通用户不需要看到：

- Raw endpoint
- CID
- GitHub token
- Retry attempt integer
- Timeout milliseconds

---

# 15. 所有用户可见字符串必须软编码

这是 beta.4 的 **P0 强制项**。

目录：

```text
src/NovaClip.App/Strings/
  zh-CN/
    Resources.resw
  en-US/
    Resources.resw
```

未来可增加：

```text
zh-TW
ja-JP
ko-KR
```

---

# 16. XAML 文本规则

禁止：

```xml
<Button Content="下载" />
<TextBlock Text="设置" />
<TextBox PlaceholderText="下载目录" />
```

必须：

```xml
<Button x:Uid="Browser_DownloadButton" />
<TextBlock x:Uid="Settings_Title" />
<TextBox x:Uid="Settings_DownloadDirectory" />
```

资源：

```text
Browser_DownloadButton.Content
Settings_Title.Text
Settings_DownloadDirectory.PlaceholderText
```

---

# 17. C# 动态文本规则

定义：

```csharp
public interface ILocalizationService
{
    string GetString(string key);
    string Format(string key, params object[] args);
}
```

禁止：

```csharp
StatusText.Text = "下载失败";
```

必须：

```csharp
StatusText.Text = _localization.GetString("Download_Error_Generic");
```

---

# 18. Error Code 与用户文字分离

内部：

```text
MEDIA_NOT_FOUND
MEDIA_PLAYURL_EXPIRED
WEBVIEW_PROCESS_FAILED
FFMPEG_NOT_FOUND
UPDATE_HASH_MISMATCH
```

UI：

```text
resource key
```

日志使用稳定英文 code。

这样未来切换语言不影响：

- 日志检索
- Telemetry（如果未来有 opt-in）
- GitHub issue
- Error handling

---

# 19. 硬编码检测 CI

新增：

```text
scripts/check-localization.ps1
```

至少扫描：

### XAML

```text
Text="
Content="
Header="
PlaceholderText="
Title="
ToolTipService.ToolTip="
```

如果存在用户可见 literal：

```text
CI failure
```

允许：

- x:Uid
- Binding
- ThemeResource
- StaticResource
- 数值
- automation id
- internal tag

### C#

检查主要 UI 项目中的：

```text
.Text = "..."
Content = "..."
Title = "..."
```

必须维护 allowlist，禁止靠“全部忽略”绕过。

---

# 20. API / Interface Surface

创建：

```text
docs/API_SURFACE.md
```

beta.4 必须先定义当前和未来的扩展边界。

---

# 21. 浏览器接口

```csharp
IBrowserSessionService
IBrowserNavigationService
IBrowserNavigationPolicy
IBrowserHistoryService
IBrowserTabService
IBrowserTab
IBrowserDiagnosticsService
IExternalUriLauncher
```

beta.4：

- 实现单 Tab。
- `IBrowserTabService` 保留多 Tab 能力。

---

# 22. Bilibili 接口

```csharp
IBilibiliPageContextProvider
IBilibiliApiClient
IBilibiliEndpointProvider
IBilibiliSessionAdapter
IBilibiliUrlResolver
IMediaDetectionCoordinator
IMediaDetectionStrategy
IPlayUrlObservationSource
IMediaResolver
IQualityResolver
ICodecResolver
```

不得把：

```text
具体 API URL
具体 query
quality mapping
codec mapping
```

散落在 UI。

---

# 23. 下载接口

```csharp
IDownloadQueueService
IDownloadTaskService
ITrackDownloader
IResumeService
IDownloadPersistence
IDownloadHistoryRepository
IDownloadProgressSource
IDownloadFileNamingService
```

---

# 24. Merge / Media Processing

```csharp
IMediaMerger
IFfmpegLocator
IFfmpegValidator
IMediaOutputValidator
```

未来：

```csharp
IAudioExtractor
IMediaTranscoder
```

beta.4 不实现转码，但留接口。

---

# 25. Settings

```csharp
ISettingsService
ISettingsStore
ISettingsMigrationService
ISettingsDefaultsProvider
```

所有 Settings 必须有：

```text
SchemaVersion
DefaultValue
Validation
Migration
```

---

# 26. Localization

```csharp
ILocalizationService
IAppLanguageService
```

---

# 27. Update

```csharp
IUpdateService
IUpdateFeed
IUpdatePackageVerifier
IUpdateInstaller
IUpdateChannelProvider
```

未来从 GitHub 切到：

```text
公开 signed manifest
CDN
Store
```

不能影响 Settings UI。

---

# 28. Windows OS Services

```csharp
IFilePickerService
IFolderLauncher
INotificationService
IClipboardService
IAppLifecycleService
IWindowService
IThemeService
```

未来：

```csharp
ITrayService
IAutoStartService
IJumpListService
```

---

# 29. Future Capability Contracts

beta.4 只定义 contract，不实现完整功能：

```csharp
IBatchMediaEnumerator
IMultiPartMediaProvider
ISubtitleProvider
IDanmakuProvider
ICoverArtProvider
IMediaMetadataProvider
IAudioTrackProvider
IPlaylistProvider
ISeasonProvider
IStreamProbeService
ISpeedLimitService
ISchedulerService
```

目的：

> 以后增加功能时添加 implementation，而不是重写 MainWindow / BrowserPage / DownloadManager。

---

# 30. Dependency 规则

允许：

```text
App
  ↓
Windows
  ↓
Infrastructure / Bilibili
  ↓
Core
  ↓
Contracts
```

禁止：

```text
Core → App
Core → WinUI
Bilibili → App
Infrastructure → BrowserPage
Windows → concrete Page
```

UI 只能通过 interface 调业务。

---

# 31. MVVM / State 规则

beta.4 不强制引入大型 MVVM 框架。

可以：

- 自己使用 `INotifyPropertyChanged`
- CommunityToolkit.Mvvm（如确有必要）

但必须统一。

每个页面至少：

```text
BrowserViewModel
DownloadsViewModel
HistoryViewModel
SettingsViewModel
```

禁止把大量业务逻辑继续塞入：

```text
BrowserPage.xaml.cs
SettingsPage.xaml.cs
```

Code-behind 只允许：

- View-specific event bridging
- focus
- animation
- raw WinUI interop

---

# 32. BrowserViewModel 状态

示例：

```text
CurrentUri
DisplayUri
PageTitle
CanGoBack
CanGoForward
IsLoading
BrowserError
MediaDetectionState
CurrentMedia
AvailableQualities
SelectedQuality
CanDownload
```

---

# 33. SettingsViewModel

每个设置：

- typed value
- validation
- help text key
- default value
- save immediately
- error state

用户修改后立即：

```text
ViewModel
→ ISettingsService
→ atomic save
→ event
→ service reacts
```

---

# 34. 下载页重构

任务卡只显示用户关心的信息：

```text
标题
清晰度
状态
进度
速度
ETA
保存路径摘要
```

操作：

```text
暂停
继续
重试
取消
打开文件
打开文件夹
```

使用：

```text
CommandBar
ContextFlyout
```

不要一次摆六七个大按钮。

---

# 35. 状态文字

内部：

```text
Queued
Resolving
DownloadingVideo
DownloadingAudio
Merging
Completed
Failed
```

UI：

```text
等待中
正在解析
正在下载
正在合并
已完成
失败
```

全部来自 Resources。

---

# 36. History 重构

历史项：

```text
标题
完成时间
清晰度
输出文件
状态
```

Context menu：

```text
打开
打开文件夹
重新下载
删除记录
```

删除记录：

- 默认不删除真实文件。
- 若要删除文件，单独明确确认。

---

# 37. App Settings 与 Runtime 分离

用户设置：

```text
settings.json
```

Runtime / cache：

```text
Cache/
Runtime/
```

数据库：

```text
Data/novaclip.db
```

浏览器：

```text
WebView2/
```

日志：

```text
Logs/
```

建议最终：

```text
%LocalAppData%\NovaClip\
  App\
  Data\
  Cache\
  Logs\
  WebView2\
  settings.json
```

---

# 38. 数据迁移

beta.4 必须兼容 beta.3。

定义：

```csharp
ISettingsMigrationService
IDatabaseMigrationService
```

启动顺序：

```text
Acquire app instance
→ Initialize logging
→ Determine data root
→ Run settings migration
→ Run DB migration
→ Initialize services
→ Create MainWindow
```

如果 Migration 失败：

```text
显示原生错误窗口
保留旧数据
不破坏数据库
```

---

# 39. Windows 11 交互规范

## 不要滥用 Modal

错误优先：

```text
InfoBar
```

只有：

- 清除账号
- 删除文件
- 重置全部数据
- 不可逆操作

使用：

```text
ContentDialog
```

---

## Keyboard

必须：

```text
Alt+Left  后退
Alt+Right 前进
Ctrl+L    地址栏
Ctrl+R/F5 刷新
Ctrl+,    设置
```

未来：

```text
Ctrl+T 多标签
Ctrl+W 关闭标签
```

---

# 40. Accessibility

P0：

- AutomationProperties.Name
- ToolTip
- Keyboard focus
- Tab order
- Narrator readable labels
- High Contrast
- 100%～250% DPI
- 不仅靠颜色传递状态
- Minimum touch target
- Text scaling 不截断

---

# 41. Responsive Layout

至少三档：

```text
>= 1200
浏览器 + 右侧媒体 pane

900～1199
媒体 pane 更窄

< 900
媒体 pane 折叠到下方 / 可展开
NavigationView compact
```

最小窗口尺寸应设置合理下限。

---

# 42. Design Tokens

不再散落 Magic Numbers。

创建：

```text
AppSpacing4
AppSpacing8
AppSpacing12
AppSpacing16
AppSpacing24
AppSpacing32

AppContentMaxWidth
AppBrowserSidePaneWidth
```

尽可能使用系统 ControlTheme 和资源。

---

# 43. Browser Download Detector 的 Debug 能力

高级设置：

```text
导出探测诊断
```

导出：

```text
App version
OS
WebView2 runtime version
Current URL
Page identifiers
Detection state transitions
PlayURL endpoint path（去敏）
Resolver result
Error codes
```

禁止：

- Cookie
- SESSDATA
- Authorization
- 完整 signed media URL query

---

# 44. Logging

使用统一 logger。

格式：

```text
timestamp
level
component
eventId
message
exception
```

例如：

```text
Browser.NavigationStarted
Browser.NewWindowIntercepted
MediaDetection.PlayUrlObserved
MediaDetection.Ready
Download.Started
Download.RangeResumed
Ffmpeg.MergeStarted
Update.PackageVerified
```

---

# 45. Startup 日志

必须至少：

```text
App.Start
Resources.Ready
Settings.Migrated
Database.Ready
Services.Ready
MainWindow.Created
Shell.Ready
BrowserPage.Ready
WebView2.Ready
App.StartupCompleted
```

CI 只在：

```text
App.StartupCompleted
```

出现后才算启动成功。

---

# 46. XAML / PRI 防回归

必须保留：

```text
resources.pri
```

发布检查。

CI：

```text
Publish
→ verify resources.pri
→ launch NovaClip.exe
→ verify App.StartupCompleted
→ navigate every top-level page
```

不能再出现：

```text
XAML parse error
但因为错误窗口还活着所以 CI 绿色
```

---

# 47. Windows UI Smoke Test

beta.4 必须比 beta.3 更严格。

Windows Runner 自动验证：

```text
MainWindow
BrowserPage
DownloadsPage
HistoryPage
SettingsPage
```

每个页面：

```text
Navigate
→ 页面构造成功
→ 记录 Page.Ready
```

任何 XAML Parse Exception：

```text
CI fail
```

---

# 48. Browser Navigation Integration Test

在 CI 中使用本地测试网页，不依赖真实 Bilibili 登录。

本地页面包含：

```html
<a target="_blank" href="/second">Open new window</a>
```

测试：

```text
点击
→ 不产生第二个 Window
→ 当前 WebView 导航到 /second
```

再测试：

```text
window.open('/third')
```

结果相同。

---

# 49. Detector 测试

使用脱敏 fixture：

```text
dash
durl
bangumi
multiple quality
backup url
permission denied
expired url
unknown codec
```

并测试：

```text
old navigation result is ignored
duplicate playurl is deduplicated
cid change triggers new generation
```

---

# 50. Settings 测试

必须测试：

```text
Concurrency ∈ {1,2,3}
```

UI 根本不允许非法值。

测试：

```text
settings migration
default values
instant save
invalid JSON fallback
atomic write
```

---

# 51. Localization 测试

CI：

```text
zh-CN key set
==
en-US key set
```

缺少 key：

```text
CI fail
```

硬编码检测失败：

```text
CI fail
```

---

# 52. API Contract 测试

至少检查：

- App 不直接依赖 concrete downloader。
- BrowserPage 不直接 new HttpClient。
- SettingsPage 不直接 File.WriteAllText。
- Bilibili implementation 不进入 UI project。
- `NovaClip.Contracts` 不依赖 Windows。

---

# 53. Release Gate

beta.4 Release 只有全部满足才创建：

## Startup

- [ ] `resources.pri` 存在。
- [ ] `App.StartupCompleted`。
- [ ] 没有 Startup Error。

## Shell

- [ ] Mica。
- [ ] TitleBar。
- [ ] NavigationView。
- [ ] Light / Dark。
- [ ] Settings 使用原生 Settings entry。

## Browser

- [ ] Bilibili 可正常浏览。
- [ ] Back。
- [ ] Forward。
- [ ] Refresh。
- [ ] Address bar。
- [ ] SPA 导航。
- [ ] target=_blank 不产生新窗口。
- [ ] window.open 不产生新窗口。
- [ ] WebView2 process failure 可恢复。

## Detection

- [ ] BV。
- [ ] 多 P 基础。
- [ ] Bangumi 基础。
- [ ] DASH。
- [ ] DURL。
- [ ] 结果去重。
- [ ] Page navigation isolation。
- [ ] 权限错误有清晰 UI。

## Settings

- [ ] 并发 1～3 使用选项而非输入框。
- [ ] Folder picker。
- [ ] FFmpeg status。
- [ ] Update channel。
- [ ] Immediate save。
- [ ] No ambiguous settings。

## Localization

- [ ] 所有用户文字软编码。
- [ ] zh-CN。
- [ ] en-US。
- [ ] hardcoded string CI gate。

## Downloads

- [ ] Pause。
- [ ] Resume。
- [ ] Retry。
- [ ] Cancel。
- [ ] FFmpeg merge。
- [ ] Clear error state。

---

# 54. beta.4 不允许出现

以下任一出现即不允许 Release：

- “输入 1～3”。
- 让用户手输文件夹作为主要方式。
- WebView2 自动弹第二个浏览器窗口。
- MainWindow 业务逻辑直接发 HTTP。
- BrowserPage 自己解析 Cookie。
- XAML 内出现大量中文/英文 literal。
- Settings 保存按钮负责保存所有设置。
- UI 用内部 enum / qn / codec id 当显示文字。
- Error 只写进日志而 UI 没提示。
- 进程活着就算启动成功。
- `resources.pri` 未验证。
- 为修 XAML 问题把整个 UI 移到 C# 动态构建。
- 为了过 CI 删除真正的验收测试。

---

# 55. 实施顺序

## Milestone 0 — Freeze Legacy

- 标记 beta.3 UI 为 Legacy。
- 建 refactor branch。
- 记录可复用代码。
- 记录必须删除/替换代码。

验收：

```text
docs/LEGACY_AUDIT.md
```

---

## Milestone 1 — New Solution Boundaries

建立：

```text
NovaClip.Contracts
NovaClip.Core
NovaClip.Bilibili
NovaClip.Infrastructure
NovaClip.Windows
NovaClip.App
```

迁移可复用核心代码。

验收：

- 跨平台 tests pass。
- dependency rules pass。

---

## Milestone 2 — Windows 11 Shell

完成：

- Mica
- TitleBar
- NavigationView
- Browser / Downloads / History / Settings navigation
- InfoBar host
- Localization bootstrapping

验收：

```text
所有页面 CI 可加载
```

---

## Milestone 3 — Localization First

不要等 UI 写完再翻译。

先创建：

```text
zh-CN Resources.resw
en-US Resources.resw
ILocalizationService
hardcode scanner
```

从此之后新增 UI 必须先加 resource key。

---

## Milestone 4 — Settings Redesign

完成设置模型、UI、即时保存、Picker。

验收：

- 1～3 不可输入非法值。
- 无 Save All。
- 所有说明明确。

---

## Milestone 5 — Browser Host

完成：

- toolbar
- navigation
- new window interception
- SPA
- ProcessFailed
- status
- external navigation policy

验收：

- 本地 integration test。
- Windows 手测 Bilibili 正常浏览。

---

## Milestone 6 — Media Detection Pipeline

完成：

- DetectionCoordinator
- generations
- PlayURL observer
- context resolver
- fallback resolver
- dedupe
- UI state

验收：

- fixtures。
- Windows 真机 BV。
- Windows 真机 Bangumi。

---

## Milestone 7 — Download Integration

把 Ready result 转为 DownloadRequest。

完成：

- quality select
- codec select
- queue
- progress
- pause/resume/retry/cancel

---

## Milestone 8 — FFmpeg UX

完成：

- detect
- choose
- test
- clear missing message
- merge

---

## Milestone 9 — History + Diagnostics

完成：

- history UX
- diagnostic export
- log redaction
- open log folder

---

## Milestone 10 — Update + Migration

完成：

- beta.3 → beta.4 migration
- update abstraction
- installer
- portable
- package verification

---

## Milestone 11 — Windows Acceptance

必须真实检查：

```text
Windows 11
100% scaling
150% scaling
Dark
Light
High Contrast
fresh install
coverage install beta.3 → beta.4
portable
```

---

# 56. 未来路线

## beta.5 — Media Reliability

重点：

- 更稳定的 Bilibili resolver。
- 多 P 正式 UI。
- Bangumi episode selector。
- expired URL transparent re-resolve。
- 更完整 codec / audio selection。

---

## beta.6 — Companion Content

通过已经预留的 provider：

- subtitles
- danmaku
- cover
- metadata

不改 Shell。

---

## beta.7 — Download Engine

- multi-range optional
- speed probe
- speed limit
- scheduler
- queue priority

---

## RC

- ARM64
- code signing
- MSIX / Store strategy
- signed public update manifest
- accessibility audit
- crash recovery
- final privacy/security review

---

## 1.0 Stable

要求：

- 原生 Windows 11 体验稳定。
- 浏览器不产生失控窗口。
- 探测可靠。
- 下载可靠。
- 更新可靠。
- 设置明确。
- 中英文资源完整。
- 公开发布链路完整。

---

# 57. 产品长期方向

NovaClip 不应成为：

```text
“塞了很多按钮的 Bilibili 工具箱”
```

而应成为：

> **Windows 上专注、可靠、原生、低干扰的 Bilibili 下载管理器。**

长期产品原则：

1. 浏览内容和创建下载任务是最重要的路径。
2. 下载状态必须清晰。
3. 用户不应该理解技术参数才能使用。
4. 高级参数隐藏在高级设置。
5. 任何新功能先问：
   - 是否属于下载工作流？
   - 是否能用现有 capability interface 接入？
   - 是否会破坏简单性？
6. 不为“功能数量”牺牲 Windows 原生体验。

---

# 58. Codex 开始执行时的最短提示词

```text
严格按照 NOVACLIP_1.0.0-beta.4_NATIVE_REBUILD_PLAN.md 执行 NovaClip 1.0.0-beta.4 Native Windows Rebuild。

这不是对 beta.3 UI 做补丁，而是重构应用层。保留经过测试的下载/解析/持久化核心能力，但重新建立 NovaClip.Contracts / Core / Bilibili / Infrastructure / Windows / App 边界，并从新的 WinUI 3 Windows 11 Shell 开始。

必须完成 Windows 11 原生设计、浏览器单窗口导航和 NewWindowRequested 处理、媒体探测 pipeline、清晰设置 UX、所有用户文字 Resources.resw 软编码、API surface 和未来 capability contracts。

不要因为当前环境是 macOS 停止。所有 WinUI/WebView2 真实编译、页面加载、resources.pri、浏览器行为和启动验证通过 GitHub Actions Windows Runner 完成。持续读取 CI 结果并修复，直到 beta.4 Release Gate 全部满足。Release Gate 未满足不得发布 beta.4。
```

---

# 59. 最终 Definition of Done

NovaClip beta.4 只有在用户第一次打开时能明显感受到：

```text
“这是一个真正按 Windows 11 方式设计的原生程序”
```

才算完成。

不仅要“功能能跑”，还必须：

- 看得懂。
- 点得明白。
- 浏览自然。
- 不乱开窗口。
- 不暴露工程字段。
- 不出现硬编码用户文字。
- 不因为 Bilibili 页面切换让探测状态错乱。
- 不让 UI 层承载业务实现。
- 不让下一项功能迫使开发者再次推翻整个 App。

**beta.4 的目标不是多加几个功能，而是让 NovaClip 从一个能编译的测试程序，变成一个有长期维护基础的 Windows 原生应用。**
