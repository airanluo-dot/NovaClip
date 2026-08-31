# NovaClip

NovaClip 是一个面向 Windows 的 Bilibili 原生下载管理器。它使用 WinUI 3 + WebView2 打开 B 站真实页面，使用原生 C# 下载引擎将 DASH 音视频流式写入磁盘，并调用本机 FFmpeg 进行无损 remux。

当前版本：**1.0.0-beta.2**

> 当前仓库用于首个测试版开发。应用只处理用户在 B 站账号下有权正常播放的内容，不实现 DRM 解密、会员权限绕过、Cookie 窃取或访问控制规避。

## 功能

- WebView2 内登录 B 站，使用应用独立且持久的浏览器配置目录。
- 识别普通 `/video/BV...`、`/video/av...` 和 `/bangumi/play/...` 页面。
- 捕获播放器的 `/playurl` 响应，兼容 DASH、DURL、`base_url`/`baseUrl`、`backup_url`/`backupUrl`。
- 直接流式写盘、Range 断点续传、CDN 候选回退和指数重试。
- 下载任务队列、暂停/继续/取消、历史记录和 SQLite 持久化。
- 使用原生 FFmpeg `-c copy` 合并音视频，不把整段媒体放入内存。
- 首个版本即支持覆盖更新：安装版下载 GitHub Release 安装器覆盖安装；便携版使用独立 `NovaClip.Updater.exe` 替换当前目录并重启。

## 系统要求

- Windows 10 1809（10.0.17763）或更高版本，x64。
- Microsoft Edge WebView2 Evergreen Runtime。
- DASH 音视频合并需要 FFmpeg：在设置中指定 `ffmpeg.exe`，或放入 `tools/ffmpeg/win-x64/`，也可以加入系统 PATH。NovaClip 会在开始 DASH 任务前明确提示缺失 FFmpeg，而不是下载完成后静默失败。

## 使用方式

1. 从 GitHub Actions artifact 或 Releases 下载 `NovaClip-win-x64` 安装版/便携版。
2. 启动 NovaClip，在浏览器页登录 B 站。
3. 打开可正常播放的 BV、AV 或番剧页面。
4. 等待“当前媒体”卡片出现轨道，选择清晰度并点击“添加到下载”。
5. 在“下载”页查看进度；合并成功后，文件会写入设置中的下载目录。

## 更新方式

“设置”页可以手动检查更新，应用启动时也会按设置自动检查。更新资产从 `airanluo-dot/NovaClip` GitHub Releases 获取：

- 通过安装器安装时：下载 `*-setup.exe`，退出应用后使用同一个安装目录覆盖更新。
- 直接运行便携版时：便携包包含 `portable.marker` 和独立更新器，下载 `*-portable.zip` 后在应用退出时替换文件并自动重启。

当前仓库为公开仓库，NovaClip 默认可直接从公开的 GitHub Releases 获取更新元数据与发布资产，无需 GitHub 访问令牌。Release 资产若提供 GitHub `digest`，下载后会先校验 SHA-256 再执行；正式分发仍建议使用可信、签名的发布资产。

## 隐私与安全

- Cookie 仅驻留在应用专属 WebView2 profile 和下载请求的内存中，不写入 SQLite，不上传到 NovaClip 服务器。
- 日志会过滤 Cookie、`Set-Cookie`、`SESSDATA`、`bili_jct` 和 Authorization。
- 应用不包含参考扩展源码、远程通知 iframe 或参考扩展的 FFmpeg WASM。
- 详细说明见 [docs/PRIVACY.md](docs/PRIVACY.md) 和 [docs/SECURITY.md](docs/SECURITY.md)。

## 架构

```text
WinUI 3 / WebView2
        ↓ versioned JSON bridge + PlayURL response observer
BiliNative.WebBridge
        ↓ normalized MediaDescriptor
BiliNative.Core
        ↓
BiliNative.Infrastructure (HTTP Range / retry / SQLite / update API)
        ↓
Windows FFmpeg process + Windows updater
```

## 开发

```powershell
dotnet restore BiliNative.sln
dotnet test BiliNative.sln -c Release
dotnet build BiliNative.sln -c Release
```

云端 macOS 环境可以编写和测试 Core、Infrastructure、WebBridge；WinUI 3、WebView2、FFmpeg 进程和安装器通过 GitHub Actions Windows Runner 构建与验证。Windows 工作流见 [.github/workflows/windows-build.yml](.github/workflows/windows-build.yml)。

## 许可

NovaClip 自身使用 MIT License。FFmpeg 若随包分发，必须另外遵守实际 FFmpeg 构建的 LGPL/GPL 条款；本仓库不直接提交第三方 FFmpeg 二进制。