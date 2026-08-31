# Bilibili 原生 Windows 下载管理器 —— Codex 可直接执行的项目规划

> 文档定位：这是项目的 **唯一主规划 / 实施规范 / 验收标准**。Codex 在执行本项目时，应以本文件为最高优先级的项目说明，并把用户提供的 `bilibili-helper-3.0.4.zip` 仅作为“行为参考实现 / 逆向参考资料”。
>
> 开发环境约束：Codex/GPT Work 云端运行在 **macOS**；最终产品是 **Windows 原生桌面应用**。因此：macOS 负责代码编写、跨平台核心测试、静态检查和仓库维护；真正的 WinUI 3 编译、打包和 Windows 专属测试必须通过 **Windows CI Runner（优先 GitHub Actions）** 完成。

---

## 0. Codex 立即执行指令

拿到本文件和 `bilibili-helper-3.0.4.zip` 后，**不要要求用户再补充技术资料再开始开发**。按以下顺序直接执行：

1. 创建/检查 Git 仓库。
2. 对 `bilibili-helper-3.0.4.zip` 做只读解压和结构审计，确认 SHA-256（若可用）。
3. 只提取“行为、接口、字段、页面兼容策略、清晰度映射”等知识；**不要把原扩展源码直接复制进产品代码**。
4. 创建本规划指定的 solution / projects / tests / scripts / CI。
5. 在 macOS 本地先完成所有跨平台 Core 层实现和测试。
6. 创建 Windows GitHub Actions 工作流，在 Windows Runner 上编译 WinUI 3 App。
7. 每完成一个里程碑都必须：
   - 运行当前环境可运行的测试；
   - 修复编译/测试错误；
   - 更新 `docs/STATUS.md`；
   - 继续下一个里程碑，不因“当前是 Mac”而停止。
8. Windows 专属问题必须通过 CI 日志反复修复，直至 CI 通过；不要把“需要 Windows”当成结束理由。
9. 第一阶段目标不是“概念 Demo”，而是一个能在 Windows 上实际登录 B 站、识别当前视频、创建下载任务、把 DASH 音视频直接写盘并调用原生 FFmpeg 合并的可运行版本。

---

# 1. 项目目标

开发一个真正的 Windows 原生 Bilibili 下载管理器，核心原则是：

- 使用 **C# + WinUI 3 + Windows App SDK + WebView2**。
- WebView2 负责 B 站真实网页、登录态、页面兼容和播放上下文。
- 原生 C# 负责解析、下载、断点续传、任务队列、重试、文件系统、历史记录和日志。
- 原生 `ffmpeg.exe` 负责音视频无损 remux；**不使用浏览器 WASM FFmpeg**。
- 媒体数据必须尽可能“网络 → FileStream → 磁盘”，避免整个视频进入 RAM。
- 优先兼容用户有权正常播放的内容；**不得实现 DRM 绕过、会员权限绕过、凭证窃取或访问控制规避**。
- 所有 B 站登录应在 WebView2 中由用户自行完成；应用不得要求用户手工复制 Cookie / SESSDATA 作为主要工作流。
- 不把用户 Cookie、播放地址、视频文件上传到开发者服务器。
- 第一正式目标平台：**Windows 10 1809+ / Windows 11，x64 优先**。

工作项目名：

- 展示名：`Bilibili Native Download Manager`
- 中文工作名：`Bilibili 原生 Windows 下载管理器`
- 内部解决方案名：`BiliNative`
- 根命名空间：`BiliNative`

如果未来公开发布，发布前应重新确认产品名称/商标策略；当前开发阶段先使用上述工作名。

---

# 2. 用户提供的唯一必需参考资料

## 2.1 必需

用户只需要向 Codex 提供：

```text
bilibili-helper-3.0.4.zip
```

本规划已经把预期行为、架构和已知逆向结论写清楚，因此 **不需要用户再提供扩展源码说明、接口文档、截图或逆向报告**。

参考 ZIP 的已知结构：

```text
bilibili-helper-content-script-seed.js
bilibili-helper-content-script.js
ffmpeg.worker.js
ffmpeg-core.js
ffmpeg-core.worker.js
ffmpeg-core.wasm
manifest.json
popup.html
icon.png
```

参考 ZIP 已知总内容：9 个文件，总解压体积约 31.7 MB，其中绝大部分是 `ffmpeg-core.wasm`。

如果需要校验参考包是否一致，可使用：

```text
bilibili-helper-3.0.4.zip
SHA-256:
95036016a004107979b179bd4cb43de76e40d95dc2ef9a020f6a3b385f54e1a4
```

主脚本已知哈希：

```text
bilibili-helper-content-script.js
SHA-256:
89474c3750f92ac9ea2fe5e099c8d8ecccb96d0cc644d989c7c62c959d95963d
```

## 2.2 可选但不是启动阻塞项

只有未来进入“品牌/发布”阶段才可向用户询问：

- App Logo / Icon
- 正式产品名
- 是否签名 MSIX
- 是否上 Microsoft Store
- 是否内置 FFmpeg 二进制还是首次启动下载
- 是否支持 ARM64

这些都 **不允许阻塞 V0.1 功能开发**。

---

# 3. 参考扩展必须提取的知识

Codex 应在开始时自行检查 ZIP，确认以下已知行为。若参考包细节略有变化，以实际 ZIP 为准；但架构目标不变。

## 3.1 参考扩展入口

`manifest.json`：

- Manifest V3
- 对 `http://*.bilibili.com/*` 和 `https://*.bilibili.com/*` 注入 content script
- `run_at = document_start`
- 没有 background/service worker
- 没有 `cookies` / `downloads` / `tabs` 等显式高权限

`bilibili-helper-content-script-seed.js`：

- 在 B 站页面加载早期执行
- 将主脚本注入 B 站 Page World
- 让主脚本可直接访问页面的 `window`、播放器状态和 XHR

Windows 版不需要照搬“扩展 isolated world → page world”的绕行；应由 WebView2 宿主直接实现页面脚本注入与网络观察。

## 3.2 参考扩展支持的视频页面

至少识别：

```text
/video/av...
/video/BV...
/bangumi/play/...
```

普通投稿视频从页面数据取得：

- `aid`
- `bvid`
- `cid`
- 标题
- 多 P 信息

页面数据来源参考：

- `window.__INITIAL_STATE__`
- `script#__NEXT_DATA__`

新版番剧兼容参考：

- `window.__PLAYURL_HYDRATE_DATA__`
- 播放器发出的 `/playurl` 网络响应

## 3.3 参考扩展普通视频 PlayURL

参考扩展调用：

```text
https://api.bilibili.com/x/player/wbi/playurl
```

已知参数形态：

```text
qn={quality}
fnver=0
fnval=4048
fourk=1
avid={aid}
bvid={bvid}
cid={cid}
```

请求使用当前 B 站登录态。

注意：不要假定这个接口和参数永久稳定。Windows 版应通过“WebView 捕获真实播放器 PlayURL”作为重要兼容后备路径，而不是完全依赖一个硬编码 API。

## 3.4 清晰度映射参考

至少兼容：

```text
16  -> 360P
32  -> 480P
64  -> 720P
80  -> 1080P
112 -> 1080P 高码率
120 -> 4K
125 -> HDR
```

但新程序的数据模型不要把清晰度限制为这几个常量，应保留未知/新增 qn 并显示服务端描述。

## 3.5 DASH / DURL 参考

PlayURL 可能包含：

```text
dash.video[]
dash.audio[]
durl[]
```

参考扩展：

- DASH 选择一个视频轨和一个音频轨
- 主要 URL 来自 `base_url` / `baseUrl`
- 备用 URL 来自 `backup_url` / `backupUrl`
- 老格式使用 `durl[]`

Windows 版必须把 `base + backups` 统一建模为候选 CDN 列表，并真正实现失败 fallback。

## 3.6 参考扩展 FFmpeg 行为

参考扩展使用 `@ffmpeg/core 0.12.1` WASM，并执行等价于：

```bash
ffmpeg -i video -i audio -vcodec copy -acodec copy output.mp4
```

Windows 版必须替换为 **原生 FFmpeg 进程**，并使用等价的无重编码 remux：

```bash
ffmpeg -hide_banner -nostdin -y \
  -i video.m4s \
  -i audio.m4s \
  -map 0:v:0 -map 1:a:0 \
  -c copy \
  output.mp4
```

必要时针对不同容器调整扩展名/参数，但默认禁止转码。

## 3.7 参考扩展已知问题 —— 新程序不得继承

已知缺陷包括：

1. `networkErrorHandler()` 被调用但未定义。
2. `beforeunload` 清理引用不存在的 `ffmpeg` 全局变量。
3. 合并输出长期留在 FFmpeg 虚拟文件系统，存在内存增长。
4. Blob URL 延迟释放。
5. 高级下载先把完整媒体读入浏览器内存，再写 WASM，内存效率差。
6. 没有真正断点续传。
7. `backup_url` 主要用于显示，没有完善自动 failover。
8. 某处分集 XHR 解析把 `bvid` 做了数值转换，可能得到 `NaN`。
9. 页面结构解析依赖固定 DOM / 固定 JSON 路径，改版后脆弱。
10. 全局替换 `window.XMLHttpRequest`，侵入性较高。
11. 没有可靠的多层 retry / checksum / resume 状态持久化。

Windows 版架构必须主动解决上述问题，而不是 1:1 复制。

---

# 4. 技术栈与版本策略

截至 2026-09，采用：

```text
Language: C#
Runtime: .NET 10
UI: WinUI 3
Windows App SDK: 2.2.x stable
Embedded Browser: WebView2
Persistence: SQLite
Logging: Microsoft.Extensions.Logging + rolling file sink or a lightweight structured logger
Tests: xUnit
CI: GitHub Actions
Target architecture V0.1: win-x64
Minimum OS: Windows 10 1809 (10.0.17763) or later
Primary UX target: Windows 11
```

版本原则：

- Windows App SDK 使用当前稳定 `2.2.x`，锁定最终实际使用的精确 NuGet 版本。
- .NET 使用 10.x SDK；仓库加入 `global.json` 锁定大版本/feature band。
- 不使用 Preview 包，除非稳定版无法实现必要功能且在 `docs/DECISIONS.md` 说明原因。
- WebView2 运行时使用 Microsoft Evergreen Runtime，不自研浏览器内核。

---

# 5. macOS 云端开发 + Windows 构建策略（强制）

这是项目最重要的工程约束之一。

## 5.1 macOS 可以做什么

在 GPT Work / Codex macOS 云端：

- 创建和编辑全部 C# / XAML / JS / YAML / Markdown。
- 运行 `BiliNative.Core`、协议解析、下载策略、序列化等跨平台测试。
- 运行 JSON fixture 测试。
- 运行格式化、静态分析。
- 运行不依赖 WebView2/WinUI 的 downloader 单元测试（使用本地测试 HTTP server / mock handler）。
- 生成 GitHub Actions workflow。
- 查看 GitHub Actions Windows 构建日志并修复。

## 5.2 macOS 不应假装能做什么

不要要求 macOS：

- 启动 WinUI 3 UI。
- 运行 Windows App SDK Runtime。
- 运行 WebView2 Windows 控件。
- 验证 MSIX 安装。
- 验证 `ffmpeg.exe` Windows 进程。

这些步骤必须交给 Windows CI 或最终 Windows 真机。

## 5.3 Windows CI Runner

优先固定：

```yaml
runs-on: windows-2025-vs2026
```

如果该 label 不可用，允许退回：

```yaml
runs-on: windows-latest
```

CI 至少执行：

```text
checkout
setup-dotnet 10.x
restore
build cross-platform projects
run unit tests
build WinUI app Release x64
publish unpackaged/self-contained artifact
upload artifact
```

所有 Windows 专属编译错误必须通过 CI 迭代解决。

---

# 6. 解决方案结构

创建：

```text
BiliNative.sln

src/
  BiliNative.Core/
  BiliNative.Infrastructure/
  BiliNative.WebBridge/
  BiliNative.App/

tests/
  BiliNative.Core.Tests/
  BiliNative.Infrastructure.Tests/
  BiliNative.WebBridge.Tests/

assets/
  js/
    bilibili-bridge.js
  icons/

reference/
  README.md
  .gitkeep

tools/
  ffmpeg/
    README.md

scripts/
  build-windows.ps1
  publish-windows.ps1
  verify-reference.sh

docs/
  ARCHITECTURE.md
  REFERENCE_EXTENSION.md
  DECISIONS.md
  PRIVACY.md
  SECURITY.md
  STATUS.md
  TESTING.md

.github/
  workflows/
    ci.yml
    windows-build.yml

Directory.Build.props
Directory.Packages.props
global.json
.editorconfig
.gitignore
README.md
LICENSE   # 只有确定项目许可证后再选；开发阶段可先写 TODO 或私有仓库说明
```

## 6.1 项目职责

### `BiliNative.Core`

必须保持跨平台、无 WinUI/WebView2 依赖。

包含：

- Domain models
- PlayURL JSON normalization
- Quality / codec models
- URL candidate selection
- filename sanitization
- task state machine
- retry policy models
- resume metadata models
- event contracts
- pure services/interfaces

Target：

```text
net10.0
```

### `BiliNative.Infrastructure`

尽量跨平台：

- `HttpRangeDownloader`
- SQLite repositories
- filesystem abstraction
- download metadata
- retry / timeout implementation
- logging adapters
- FFmpeg service interface的通用部分

Windows-only FFmpeg process adapter可用条件编译或单独放 App/Windows 子目录。

### `BiliNative.WebBridge`

核心的“B 站网页兼容层”：

- 页面识别 JS
- WebView2 message DTO
- PlayURL response parser
- Web response interception coordinator
- JS ↔ C# message schemas

纯 JSON/DTO 部分必须可在 macOS 单元测试。

### `BiliNative.App`

Windows-only：

```text
net10.0-windows10.0.17763.0
```

包含：

- WinUI 3 pages / controls
- WebView2 host
- CoreWebView2 events
- CookieManager adapter
- native FFmpeg process
- Windows file pickers
- Windows notifications
- app lifecycle
- dependency injection composition root

---

# 7. 产品 UX 范围

## 7.1 主导航

第一版使用 `NavigationView`：

```text
浏览器
下载
历史
设置
关于
```

## 7.2 浏览器页

布局：

```text
┌────────────────────────────────────────────┐
│ ←  →  ↻  [地址栏................] [打开] │
├──────────────────────────┬─────────────────┤
│                          │ 当前媒体        │
│                          │                 │
│        WebView2          │ 标题            │
│      bilibili.com        │ 分P/集数        │
│                          │ 清晰度          │
│                          │ 视频编码        │
│                          │ 音频编码        │
│                          │ 预计大小        │
│                          │                 │
│                          │ [添加到下载]    │
└──────────────────────────┴─────────────────┘
```

要求：

- 默认主页：`https://www.bilibili.com/`
- 支持后退/前进/刷新。
- 登录流程完全在 WebView2 中完成。
- WebView2 profile 持久化，正常情况下重启后保持登录。
- 地址栏支持粘贴 BV/AV/Bangumi URL。
- 非 bilibili.com 页面默认不注入业务 bridge。

## 7.3 当前媒体卡片

展示：

- 标题
- BV / AV / CID（可在高级信息折叠区）
- 分 P / EP
- 当前网页 URL
- 可用清晰度列表
- 视频 codec（AVC / HEVC / AV1 等）
- 音频轨
- estimated size（有则显示）
- 解析来源：`PageData` / `PlayUrlResponse` / `HydrateData`

## 7.4 下载页

每个任务展示：

```text
标题
清晰度 / codec
状态
总体进度
视频进度
音频进度
速度
ETA
保存位置
[暂停] [继续] [取消] [打开文件夹]
```

状态：

```text
Queued
Resolving
DownloadingVideo
DownloadingAudio
DownloadingSegments
Paused
Merging
Finalizing
Completed
Failed
Cancelled
```

允许：

- 多任务队列
- V0.1 默认并发下载任务 = 2
- 单任务内部 DASH 视频/音频可并行 = 2 streams
- 设置中可调 1~3

## 7.5 历史页

展示：

- 标题
- 下载时间
- 输出路径
- 清晰度
- 状态
- 重新下载
- 打开文件
- 打开文件夹
- 从历史删除记录（不默认删实际文件）

## 7.6 设置页

至少：

```text
下载目录
同时下载任务数
默认选择最高可用清晰度 / 跟随播放器清晰度
视频编码偏好（Auto / AVC / HEVC / AV1）
下载完成自动合并
合并后删除临时音视频
失败重试次数
连接超时
是否保存 debug 日志
FFmpeg 路径
清除 WebView2 登录数据
```

---

# 8. WebView2 设计

## 8.1 Profile

必须使用应用专属、持久 WebView2 user data folder。

不得读取系统 Edge/Chrome 的 Cookie 数据库。

目标：

```text
用户第一次打开应用
→ WebView2 登录 B站
→ Cookie 保存到应用自己的 WebView2 profile
→ 之后正常保持登录
```

## 8.2 JS 注入

使用：

```text
CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(...)
```

注入 `assets/js/bilibili-bridge.js`。

JS 仅做：

- 判断当前是否 B 站视频/番剧页面
- 读取可公开从页面获得的媒体上下文
- 监听 SPA URL 变化
- 监听播放器清晰度变化
- 读取 `__INITIAL_STATE__` / `__NEXT_DATA__` / `__PLAYURL_HYDRATE_DATA__`（若存在）
- 通过 `window.chrome.webview.postMessage()` 向 C# 报告结构化数据

JS **不要负责**：

- 大文件下载
- FFmpeg
- Blob 保存
- SQLite
- 原生文件系统

## 8.3 SPA 监听

不要用原扩展的“每秒轮询 URL”作为唯一方式。

优先组合：

- WebView2 `SourceChanged` / `NavigationCompleted`
- JS 包装 `history.pushState` / `replaceState`
- `popstate`
- 必要时低频 fallback 检查

避免重复注册 observer。

## 8.4 PlayURL 捕获

优先在宿主侧使用：

```text
CoreWebView2.WebResourceResponseReceived
```

筛选 request URI 包含：

```text
/playurl
```

取得响应后：

```text
Response.GetContentAsync()
```

读取小型 JSON 响应并解析。

注意：这个事件用于观察响应，不用于下载大媒体内容。媒体本身继续由原生 downloader 使用 CDN URL 直接下载。

要求：

- 只读取匹配 PlayURL 的 JSON 响应。
- JSON size 增加合理上限，例如 10 MB。
- 捕获失败时不影响 B 站网页正常播放。
- 不替换全局 `window.XMLHttpRequest` 作为第一方案。
- 如未来 B 站改用 fetch / protobuf / 新接口，可再加 JS hook 作为兼容后备，但必须隔离并可开关。

## 8.5 Cookie 传递

原生下载器如确实需要同登录态请求，应通过：

```text
CoreWebView2.CookieManager.GetCookiesAsync(...)
```

将当前 B 站相关 Cookie 转换为 downloader 的临时 `CookieContainer`。

安全要求：

- 不写日志。
- 不写 SQLite。
- 不明文持久化到额外配置文件。
- 仅驻留内存。
- Debug 日志必须过滤 `Cookie` / `Set-Cookie` / Authorization。

---

# 9. Resolver 设计

定义接口：

```csharp
public interface IMediaResolver
{
    Task<MediaDescriptor> ResolveAsync(MediaResolveContext context, CancellationToken cancellationToken);
}
```

核心模型示例：

```text
MediaDescriptor
  Id
  PageUrl
  Title
  PartTitle
  CoverUrl?
  Aid?
  Bvid?
  Cid?
  EpisodeId?
  MediaType
  AvailableQualities[]
  SelectedQuality
  VideoTracks[]
  AudioTracks[]
  LegacySegments[]
  Source
```

### `MediaTrack`

```text
TrackType: Video | Audio
QualityId?
QualityLabel?
CodecId?
CodecName?
Bandwidth?
Width?
Height?
FrameRate?
MimeType?
EstimatedSize?
Urls[]  // first = primary, remaining = backups
```

## 9.1 Normalization 规则

必须兼容 B 站字段命名变体：

```text
base_url / baseUrl
backup_url / backupUrl
```

不得直接把 JSON 绑死到一套 camelCase。

## 9.2 视频轨选择

默认：

1. 匹配用户选择的 quality。
2. 在同 quality 中按 codec 偏好。
3. 如果用户选择 Auto：优先兼容性较好的 codec，具体顺序写入设置并可修改。
4. 找不到精确 quality 时明确降级并在 UI 提示，不静默伪装成目标清晰度。

## 9.3 音频轨选择

V0.1：

- 默认选择普通 `dash.audio[]` 中最高合适带宽轨。
- 不能像参考扩展那样永远只写死 `[0]` 而不记录选择依据。

后续：

- Dolby
- FLAC / Hi-Res
- 多音轨

必须留扩展模型，但 V0.1 不强求全部实现。

---

# 10. 下载引擎

核心接口：

```csharp
public interface IDownloadEngine
{
    Task DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);
}
```

## 10.1 直接写盘

禁止：

```text
ReadAsByteArrayAsync() 整个大视频
MemoryStream 保存整个文件
Blob
WASM MEMFS
```

必须：

```text
HttpClient
→ ResponseHeadersRead
→ Stream
→ FileStream
```

建议 buffer：64 KB~1 MB 范围，根据测试确定。

## 10.2 临时文件

格式：

```text
<download-folder>/.bilinative/<task-id>/video.m4s.part
<download-folder>/.bilinative/<task-id>/audio.m4s.part
<download-folder>/.bilinative/<task-id>/task.json
```

完成后：

```text
video.m4s.part -> video.m4s
audio.m4s.part -> audio.m4s
```

任务完成且合并成功后可按设置删除临时目录。

## 10.3 断点续传

下载开始前：

1. 检查 `.part` 文件。
2. 读取本地长度。
3. 若 > 0，尝试：

```http
Range: bytes={localLength}-
```

4. 服务端返回 `206 Partial Content` 才追加写入。
5. 若服务端返回 `200`，根据策略重新下载，不能直接 append 造成损坏。
6. 检查 `Content-Range`。
7. 将源 URL / expected length / ETag / Last-Modified（若可用）写入 `task.json`。
8. 如果远程资源身份变化，丢弃不安全 resume。

## 10.4 CDN fallback

每条轨保存：

```text
Urls = [base, backup1, backup2, ...]
```

失败规则：

- DNS / connection reset
- timeout
- 5xx
- 特定可恢复 403/412 场景

切换到下一个候选 URL，并保持已下载 offset（若候选 URL 支持 Range 且内容一致）。

对 401/账号权限失败不要无限重试。

## 10.5 Retry

默认：3 次，可设置。

建议指数退避：

```text
1s
2s
4s
```

加入小范围 jitter。

可恢复错误和不可恢复错误要分类。

## 10.6 Pause / Resume

Pause：

- 取消当前 HTTP 操作。
- 保留 `.part` 和 task metadata。
- 状态变 `Paused`。

Resume：

- 重新解析有效下载 URL（因为 CDN URL 可能过期）。
- 根据轨道 identity 继续 Range。

这点很重要：不要假定数小时前的 CDN URL 永远有效。

---

# 11. FFmpeg 服务

接口：

```csharp
public interface IFfmpegService
{
    Task<FfmpegResult> MergeAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        IProgress<FfmpegProgress>? progress,
        CancellationToken cancellationToken);
}
```

## 11.1 V0.1 FFmpeg 获取策略

不要把参考扩展中的：

```text
ffmpeg-core.js
ffmpeg-core.wasm
ffmpeg.worker.js
```

放进新产品。

开发阶段实现以下顺序查找：

1. App 设置中用户指定路径。
2. App 目录 `tools/ffmpeg/win-x64/ffmpeg.exe`。
3. 系统 PATH 中的 `ffmpeg.exe`。

如果找不到：

- UI 显示明确错误。
- 提供“选择 FFmpeg”按钮。
- 不阻塞其它下载功能；允许保存分离轨道。

**不要在尚未完成许可证策略前随意把第三方 FFmpeg Windows 二进制提交进 Git 仓库。**

后续发布阶段可增加经过 SHA-256 校验的自动下载或合规捆绑。

## 11.2 命令

默认：

```bash
ffmpeg.exe -hide_banner -nostdin -y \
  -i "video.m4s" \
  -i "audio.m4s" \
  -map 0:v:0 \
  -map 1:a:0 \
  -c copy \
  "output.mp4"
```

必须：

- `UseShellExecute = false`
- 重定向 stderr
- 正确处理路径引号
- 支持 CancellationToken：取消时 kill process tree
- 检查 exit code
- 确认输出文件存在且 > 0
- 失败时保留原音视频文件供恢复

不得在默认流程执行重编码。

---

# 12. 文件命名

实现统一：

```csharp
IFileNameSanitizer
```

Windows 非法字符：

```text
< > : " / \ | ? *
```

同时处理：

- 尾随空格/句点
- Windows reserved names：`CON`, `PRN`, `AUX`, `NUL`, `COM1`...`LPT9`
- 超长文件名
- 重名自动 suffix：`(1)`, `(2)`

默认命名：

普通单 P：

```text
{title}.mp4
```

多 P：

```text
{title}_P{page}_{part}.mp4
```

番剧：

```text
{series}_{episode}_{episodeTitle}.mp4
```

---

# 13. SQLite 数据库

文件位置放入应用 LocalAppData。

至少表：

```text
DownloadTasks
DownloadTracks
DownloadHistory
AppSettings   # 也可使用 Windows settings，但敏感数据禁止
```

### DownloadTasks

```text
Id
PageUrl
Title
Status
CreatedAt
UpdatedAt
OutputPath
SelectedQualityId
SelectedCodec
ErrorCode?
ErrorMessage?
```

### DownloadTracks

```text
TaskId
TrackType
ExpectedLength?
DownloadedLength
TempPath
PrimaryUrlFingerprint
ETag?
LastModified?
```

URL 全文是否长期保存需谨慎；如果 URL 带短期鉴权参数，建议只为恢复任务短期保存，完成后清除。

不得保存 Cookie。

---

# 14. 错误系统

创建统一错误码，例如：

```text
RESOLVE_PAGE_UNSUPPORTED
RESOLVE_PLAYURL_NOT_FOUND
RESOLVE_LOGIN_REQUIRED
RESOLVE_VIP_REQUIRED
RESOLVE_PARSE_CHANGED
DOWNLOAD_HTTP_ERROR
DOWNLOAD_RANGE_REJECTED
DOWNLOAD_DISK_FULL
DOWNLOAD_ACCESS_DENIED
DOWNLOAD_URL_EXPIRED
FFMPEG_NOT_FOUND
FFMPEG_FAILED
OUTPUT_CONFLICT
WEBVIEW_RUNTIME_MISSING
```

每个错误包含：

```text
Code
UserMessage
TechnicalMessage
Recoverable
SuggestedAction
Exception? (日志内部)
```

UI 不直接展示原始 stack trace。

---

# 15. 日志与隐私

## 15.1 日志

日志默认记录：

- app version
- task id
- 页面 URL（可在隐私模式下只记录 BVID）
- resolver strategy
- qn/codec
- HTTP status
- bytes/speed
- retry
- FFmpeg exit code

绝不记录：

```text
Cookie header
Set-Cookie
SESSDATA
bili_jct
Authorization
完整敏感 token
```

实现 `SensitiveDataRedactor`。

## 15.2 外部网络

V0.1 不添加自有遥测服务器。

允许的网络：

- Bilibili 页面/API/CDN
- WebView2 自身正常运行网络
- 后续用户明确选择的更新服务

不要复制参考扩展的 `csser.top` iframe。

## 15.3 安全边界

应用只处理用户当前可正常访问/播放的媒体。

不要加入：

- DRM key 获取
- Widevine 破解
- 会员绕过
- Cookie 窃取
- 浏览器其它 profile Cookie 提取
- 账号暴力登录
- 签名风控绕过工具链

如果内容服务端明确拒绝播放权限，应向用户显示对应权限错误。

---

# 16. 参考扩展源码的使用规则

Codex 必须遵守：

### 可以做

- 阅读 ZIP。
- 记录接口字段。
- 记录清晰度映射。
- 记录页面数据路径。
- 记录事件触发顺序。
- 对比 B 站当前页面行为。
- 写自己的 clean-room C# / JS 实现。

### 不可以直接做

- 把原 `bilibili-helper-content-script.js` 复制到 `assets/js` 并改名。
- 打包原作者 UI。
- 打包 `csser.top` 远程页面。
- 直接重新发布原 icon。
- 直接重新发布原 FFmpeg WASM 文件。

原因：ZIP 未提供明确 LICENSE，且新项目目标本身就是独立原生重写。

---

# 17. 关键类清单

至少实现以下对象（名称可微调，但职责必须保留）：

## Core

```text
MediaDescriptor
MediaTrack
MediaUrlCandidate
QualityOption
CodecOption
LegacyMediaSegment
DownloadTaskState
DownloadTaskSnapshot
DownloadProgress
TrackProgress
ResolveResult
AppError
RetryPolicy
```

## Services / interfaces

```text
IMediaResolver
IPlayUrlNormalizer
IDownloadManager
IDownloadEngine
IDownloadTaskRepository
IHistoryRepository
IFfmpegService
IFileNameSanitizer
IClock
IAppLogger
```

## Infrastructure

```text
PlayUrlNormalizer
HttpRangeDownloader
DownloadManager
SqliteDownloadTaskRepository
SqliteHistoryRepository
FileNameSanitizer
RetryExecutor
```

## WebBridge

```text
BilibiliPageContext
BilibiliBridgeMessage
BilibiliBridgeMessageType
PlayUrlCapture
BilibiliJsonParser
```

## Windows App

```text
WebViewHostService
WebViewCookieProvider
WebResourcePlayUrlObserver
WindowsFfmpegService
WindowsDownloadFolderService
MainWindow
BrowserPage
DownloadsPage
HistoryPage
SettingsPage
AboutPage
```

---

# 18. JS Bridge 消息协议

采用 versioned JSON schema。

示例：

```json
{
  "schemaVersion": 1,
  "type": "pageContextChanged",
  "payload": {
    "url": "https://www.bilibili.com/video/BV...",
    "kind": "video",
    "aid": 123,
    "bvid": "BV...",
    "cid": 456,
    "page": 1,
    "title": "..."
  }
}
```

其它 type：

```text
pageContextChanged
playerQualityChanged
hydrateDataFound
bridgeReady
bridgeError
```

C# 必须验证：

- schema version
- message type
- payload size
- URL origin

不要执行来自网页的任意命令字符串。

---

# 19. 下载任务状态机

合法转换示例：

```text
Queued -> Resolving
Resolving -> DownloadingVideo / DownloadingAudio / DownloadingSegments
Downloading* -> Paused
Paused -> Resolving
Downloading* -> Merging
Merging -> Finalizing
Finalizing -> Completed
任意活跃状态 -> Failed
任意活跃状态 -> Cancelled
```

状态转换必须集中在 `DownloadManager`，不要让 UI 随意 set state。

App 崩溃/关闭后：

- `Completed/Cancelled` 保持。
- 活跃任务在下次启动恢复为 `Paused` 或 `QueuedForResume`。
- 不自动盲目继续旧 CDN URL，应先重新 Resolve。

---

# 20. 关闭应用行为

如果存在下载：

V0.1 默认弹出：

```text
当前还有下载任务。
[暂停并退出] [取消]
```

“暂停并退出”：

- cancel HTTP operation
- flush streams
- persist offsets
- 保留 `.part`
- 不删除临时文件

后续可加最小化托盘后台下载，但 V0.1 非必须。

---

# 21. 测试策略

## 21.1 macOS 必须运行的测试

### PlayURL fixture tests

从参考扩展结构人工构造 fixtures：

```text
tests/fixtures/playurl/dash-basic.json
tests/fixtures/playurl/dash-backup-urls.json
tests/fixtures/playurl/durl.json
tests/fixtures/playurl/vip-error.json
tests/fixtures/playurl/unknown-fields.json
```

测试：

- `base_url` / `baseUrl`
- `backup_url` / `backupUrl`
- unknown quality
- multiple codecs
- empty audio
- durl
- malformed JSON

### Filename tests

覆盖 Windows 保留字符与保留名。

### Resume tests

使用 custom `HttpMessageHandler` 模拟：

- 200 fresh
- 206 resume
- server ignores Range
- content length mismatch
- retry 500 -> success
- primary CDN fail -> backup success

### State machine tests

保证非法转换被拒绝。

## 21.2 Windows CI tests

至少：

- WinUI project compile
- XAML compile
- WebView2 references resolve
- WindowsFfmpegService process invocation unit test（可使用 fake executable/script）
- publish artifact

如果 GitHub-hosted runner 无法进行真实 UI/WebView2 交互，不把 E2E 自动 UI 测试作为 V0.1 blocker；但必须保留 `docs/TESTING.md` 中的 Windows 手工验收清单。

---

# 22. Windows 手工验收清单

最终用户/开发者在 Windows 真机执行：

1. App 能启动。
2. WebView2 Runtime 缺失时给出明确提示。
3. 打开 B 站首页。
4. 扫码/密码正常登录。
5. 重启 App 后登录态保持。
6. 打开普通 BV 视频。
7. 当前媒体卡片出现标题/BVID/CID。
8. 能捕获可用清晰度。
9. 创建 1080P 下载。
10. 视频/音频直接写 `.part` 到磁盘。
11. 暂停。
12. 重启 App。
13. 恢复任务。
14. Range 续传成功。
15. 下载完调用 ffmpeg。
16. 生成可播放 MP4。
17. 临时文件按设置清理。
18. 打开多 P，名称正确。
19. 打开番剧页面，在账号有播放权限时可解析。
20. 无会员权限时明确显示权限错误，而非崩溃。
21. 网络断开后 retry，恢复网络后能继续。
22. 主 CDN 失败可 fallback。
23. 大文件下载时进程内存不随文件大小线性增长到数 GB。

---

# 23. CI/CD

## 23.1 `ci.yml`

在 macOS/Ubuntu 任一环境运行跨平台：

```text
restore Core/Infrastructure tests
format check
unit tests
```

## 23.2 `windows-build.yml`

触发：

```text
push main
pull_request
workflow_dispatch
```

执行：

```text
runs-on: windows-2025-vs2026
.NET 10
restore
build Release x64
run tests
publish
zip output
upload-artifact
```

Artifact 名：

```text
BiliNative-win-x64-<commit>.zip
```

## 23.3 部署模式

V0.1 推荐：

```text
Unpackaged + Windows App SDK self-contained
```

理由：

- CI 产出方便。
- 内测无需先解决证书/MSIX 签名。
- 便于附带资源目录。

后续 V1.0 再增加：

- MSIX
- 签名
- 自动更新
- Store packaging

不要在 V0.1 被 MSIX 证书流程卡住。

---

# 24. 依赖注入与 MVVM

可以使用：

```text
Microsoft.Extensions.DependencyInjection
CommunityToolkit.Mvvm
```

但避免过度工程化。

页面 ViewModel：

```text
BrowserViewModel
DownloadsViewModel
HistoryViewModel
SettingsViewModel
```

UI 不直接发 HTTP、不直接解析 B 站 JSON、不直接启动 FFmpeg。

---

# 25. 性能目标

V0.1 非硬实时，但应满足：

- 4 GB 视频下载过程中，应用 RAM 不应接近 4 GB 级别，仅因为下载文件本身而线性增长。
- UI 更新频率限制在合理区间（例如 4~10 次/秒以内）。
- 数据库进度不要每个网络 chunk 都写；例如每 1~2 秒或关键状态写一次。
- FFmpeg remux 应保持 `-c copy`。
- 取消下载应在数秒内响应。

---

# 26. 解析兼容策略

不要设计成“只有一个解析路径”。

优先级建议：

```text
Strategy A: 捕获播放器真实 PlayURL response
Strategy B: 从页面 hydrate / initial state 提取上下文后主动请求 PlayURL
Strategy C: 页面 JS 上报可用 hydrate data
```

如果 Strategy A 已经取得完整 PlayURL，优先使用它，因为它最接近当前真实播放器行为。

每次解析结果记录：

```text
ResolverSource
CapturedAt
PageUrl
```

不记录敏感 Cookie。

---

# 27. 对 B 站接口变化的防御

实现时：

- JSON 使用 tolerant parsing。
- `JsonExtensionData` 或 `JsonDocument` 保留未知字段能力。
- 字段缺失返回 typed error，不 `NullReferenceException`。
- 对数组为空做显式判断。
- 解析层写 fixture 回归测试。
- 新接口只新增 adapter，不大改下载器。

`IPlayUrlNormalizer` 应与“PlayURL 从哪里来”解耦。

---

# 28. V0.1 明确不做

为了让 Codex 能交付完整可用第一版，下列功能不要扩散范围：

- 批量下载 UP 主全部视频
- 搜索 B 站
- 弹幕下载
- 字幕下载
- 评论下载
- 直播下载
- 课程下载
- 自动绕过地区限制
- DRM 解密
- 会员绕过
- 登录自动化
- GPU 转码
- 视频剪辑
- 音频格式转换
- 下载后媒体库刮削
- 云同步
- 自有账号系统
- 遥测后台

这些全部进入 future backlog。

---

# 29. Future Backlog

V0.1 通过后可逐步：

### V0.2

- 多 P 批量选择
- 番剧选集
- 下载封面
- 下载字幕
- 下载弹幕 XML/ASS

### V0.3

- Dolby / FLAC 音轨
- codec 详细选择
- CDN speed probe
- 多段并行 Range

### V0.4

- ARM64 Windows
- 托盘后台下载
- Windows Toast

### V1.0

- MSIX + code signing
- 自动更新
- 完整恢复机制
- FFmpeg 合规内置/管理
- 正式隐私声明

---

# 30. FFmpeg 许可证要求

FFmpeg 默认采用 LGPL 2.1+，但如果构建启用 GPL 组件，整个 FFmpeg 构建可能适用 GPL。

因此发布前：

- 明确实际使用的 FFmpeg build 来源和 configure flags。
- 保存版本与 license 文本。
- 不声称 FFmpeg 是本项目自研。
- 如果分发二进制，完成对应 LGPL/GPL 合规要求。

开发 V0.1 时允许用户提供自己的 `ffmpeg.exe`，以免许可证/分发阻塞功能开发。

---

# 31. README 必须写清楚

项目 `README.md` 第一版至少包含：

- 项目是什么
- 当前状态
- Windows 系统要求
- 如何从 GitHub Actions 下载最新构建
- FFmpeg 需求
- 如何使用：打开 B 站 → 登录 → 打开视频 → 选择轨道 → 下载
- 数据隐私说明
- 不支持 DRM/权限绕过
- 架构概要
- macOS 开发环境说明：Mac 可写 Core，但 Windows UI 由 CI 构建

---

# 32. Codex 工作纪律

Codex 执行本项目必须：

1. **直接实现，不只输出建议。**
2. 不因当前主机是 macOS 而停止 Windows 项目工作。
3. Windows 编译用 CI 验证。
4. 如果某个 WinUI API 不确定，查 Microsoft 官方文档再实现。
5. 每次修改 CI 后读取构建错误并继续修复。
6. 不把“无法在 Mac 启动 WinUI”当成任务完成。
7. 不用未验证的伪 API。
8. 不把下载数据全部读入内存。
9. 不复制参考扩展的已知 bug。
10. 对外部变化（B 站字段、WinUI package）使用兼容层。
11. 新增依赖必须有明确作用，避免依赖膨胀。
12. 不在日志打印 Cookie。
13. 不把参考 ZIP 直接纳入发布产物。
14. 不擅自向用户索要账号密码/Cookie。
15. 如果用户没有额外设计要求，使用标准 Windows 11 Fluent 风格，不因 UI 细节阻塞核心功能。

---

# 33. 实施里程碑

## Milestone 0 — Repository Bootstrap

完成：

- solution / projects
- Directory.Build.props
- Directory.Packages.props
- global.json
- .editorconfig
- README
- docs skeleton
- GitHub Actions skeleton

验收：

- Core 空项目在 macOS `dotnet test` 通过。
- Windows CI 至少能开始 restore/build。

## Milestone 1 — Reference Reverse Engineering

完成：

- 解压用户 ZIP 到临时工作目录。
- 写 `docs/REFERENCE_EXTENSION.md`。
- 记录页面类型、PlayURL、qn、DASH、FFmpeg 参考行为。
- 不复制原业务代码。

验收：

- 文档能够解释参考扩展如何从页面到媒体 URL。

## Milestone 2 — Core Domain + PlayURL Normalizer

完成：

- models
- tolerant JSON parser
- track selection
- filename sanitizer
- error types
- fixtures/tests

验收：

- macOS 全部 Core tests pass。

## Milestone 3 — Downloader

完成：

- streaming file download
- progress
- retry
- range resume
- fallback URL
- cancellation
- task metadata

验收：

- mocked 200 / 206 / 500 / fallback tests pass。
- 大测试流不会完整缓存进内存。

## Milestone 4 — Persistence + Download Manager

完成：

- SQLite
- task state machine
- app restart recovery
- history

验收：

- repository tests pass。
- paused task reload preserved。

## Milestone 5 — WinUI Shell + WebView2

完成：

- MainWindow
- NavigationView
- BrowserPage
- WebView2 persistent profile
- address bar
- bridge script injection
- page context messages

验收：

- Windows CI compile success。
- Windows 手测能够打开 B 站。

## Milestone 6 — PlayURL Capture

完成：

- `WebResourceResponseReceived`
- `/playurl` filter
- response `GetContentAsync`
- normalizer feed
- current media card

验收：

- Windows 真机普通 BV 视频能显示可用 tracks。

## Milestone 7 — Native Download Integration

完成：

- 从 UI 创建 DownloadTask
- WebView Cookie -> HttpClient temporary CookieContainer
- 下载页显示实时进度
- pause/resume/cancel

验收：

- Windows 真机可完整下载 DASH 视频和音频。

## Milestone 8 — Native FFmpeg

完成：

- FFmpeg path discovery
- Process invocation
- merge
- output validation
- temp cleanup

验收：

- 下载结束得到正常可播放 MP4。

## Milestone 9 — Bangumi Compatibility

完成：

- bangumi page context
- PlayURL capture
- episode naming
- VIP/permission error handling
- hydrate fallback

验收：

- 用户有权限播放的番剧可正常解析/下载。
- 无权限明确报错。

## Milestone 10 — Stabilization

完成：

- logging redaction
- disk full
- URL expired recovery
- duplicate filename
- shutdown pause
- docs
- Windows build artifact

验收：

- CI green
- `BiliNative-win-x64-<commit>.zip` artifact 可下载
- STATUS 写明已实现/未实现

---

# 34. Definition of Done：V0.1

只有以下全部满足才叫 V0.1 完成：

- [ ] WinUI 3 原生 Windows 应用可编译。
- [ ] GitHub Actions Windows 构建绿色。
- [ ] 提供可下载 win-x64 artifact。
- [ ] WebView2 可以登录 B 站。
- [ ] 登录状态可保持。
- [ ] 普通 BV 视频可识别。
- [ ] 可取得 PlayURL/DASH 轨道。
- [ ] 可选择清晰度。
- [ ] 下载直接流式写磁盘。
- [ ] 支持进度。
- [ ] 支持取消。
- [ ] 支持暂停/断点续传。
- [ ] 支持 CDN fallback。
- [ ] 支持失败 retry。
- [ ] 支持 FFmpeg `-c copy` 合并。
- [ ] 文件名合法。
- [ ] 下载历史持久化。
- [ ] App 重启可恢复未完成任务。
- [ ] 番剧至少有基本支持。
- [ ] 没有 Cookie 明文日志。
- [ ] 没有把媒体完整放 RAM 的实现。
- [ ] README / ARCHITECTURE / PRIVACY / TESTING 完成。

---

# 35. 首次给 Codex 的最短启动提示词

如果本文件已经放在仓库根目录，例如：

```text
PROJECT_PLAN.md
```

用户只需把 `bilibili-helper-3.0.4.zip` 一并提供，然后对 Codex 说：

```text
严格按照 PROJECT_PLAN.md 从头开始实现整个项目。
bilibili-helper-3.0.4.zip 只作为行为参考实现，请先审计它，然后做 clean-room 原生 Windows 重写。
你的运行环境是 macOS，但目标是 Windows；所有 Windows 专属编译通过规划中的 GitHub Actions Windows Runner 验证。不要因为本地不能运行 WinUI 而停止。持续实现、测试、修复 CI，直到规划中的 V0.1 Definition of Done 尽可能全部完成。
```

除此之外，V0.1 启动阶段**不需要再给 Codex 提供任何额外参考资料**。

---

# 36. 实现时可查阅的权威资料（Codex 自行查，不要求用户提供）

优先官方文档：

- Microsoft Windows App SDK / WinUI 3
- Microsoft WebView2
- .NET 10
- GitHub Actions hosted runner docs
- SQLite / Microsoft.Data.Sqlite
- FFmpeg 官方 legal/license 页面

当前规划依据的关键平台事实：

- WinUI 3 是 Microsoft 推荐的新原生 Windows 桌面 UI 方案。
- Windows App SDK 2.2 支持 Windows 10 1809（build 17763）及更高版本。
- 当前 WinUI command-line 开发路径以 .NET 10 为基线。
- WebView2 提供页面创建前 JS 注入、WebResourceResponseReceived、Response.GetContentAsync、CookieManager.GetCookiesAsync。
- GitHub Actions 当前提供 Windows Server 2025 / Visual Studio 2026 hosted runner，可用于云端 Windows 编译。

---

# 37. 最终架构图

```text
┌──────────────────────────────────────────────────────────┐
│                       WinUI 3 App                        │
│                                                          │
│  BrowserPage   DownloadsPage   HistoryPage   Settings    │
└──────────────┬──────────────────────┬─────────────────────┘
               │                      │
               ▼                      ▼
┌──────────────────────────┐   ┌────────────────────────────┐
│         WebView2         │   │      DownloadManager       │
│                          │   │                            │
│  bilibili.com            │   │ Queue / State / Resume     │
│  user login              │   └─────────────┬──────────────┘
│  real player context     │                 │
└───────┬─────────┬────────┘                 ▼
        │         │                ┌────────────────────────┐
        │         │                │ HttpRangeDownloader    │
        │         │                │                        │
        │         │                │ ResponseHeadersRead    │
        │         │                │ Range / Retry / CDN    │
        │         │                └──────────┬─────────────┘
        │         │                           │
        │         │                    network → disk
        │         │                           │
        │         │                 ┌─────────┴──────────┐
        │         │                 │ video.m4s          │
        │         │                 │ audio.m4s          │
        │         │                 └─────────┬──────────┘
        │         │                           ▼
        │         │                 ┌────────────────────┐
        │         │                 │ Native ffmpeg.exe  │
        │         │                 │      -c copy       │
        │         │                 └─────────┬──────────┘
        │         │                           ▼
        │         │                      output.mp4
        │         │
        │         └── CookieManager ──→ temporary cookies
        │
        ├── JS Bridge → page context
        │
        └── WebResourceResponseReceived
                 │
                 ▼
            /playurl JSON
                 │
                 ▼
        ┌──────────────────────┐
        │ PlayUrlNormalizer    │
        │ Quality/Track models │
        └──────────────────────┘
```

---

# 38. 最重要的最终原则

**参考扩展负责告诉我们“B 站网页和播放器是怎么暴露当前可播放媒体的”；新 Windows 程序则必须把真正的下载、恢复、磁盘写入、任务管理和合并全部原生化。**

不要做“套壳扩展”。

目标是：

```text
真实 WebView2 登录环境
+ 稳健 PlayURL 兼容层
+ 原生低内存下载器
+ 断点续传
+ CDN fallback
+ 原生 FFmpeg remux
+ WinUI 3 下载管理体验
```

这才是本项目的最终技术方向。
