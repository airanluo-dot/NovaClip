# Decisions

## 1.0.0-beta.1

- Use WinUI 3 + WebView2 for a native Windows shell while retaining Bilibili page compatibility.
- Use WebView2 `WebResourceResponseReceived` for small `/playurl` responses; do not intercept or proxy large media responses.
- Store task metadata in SQLite under LocalAppData. Never store Bilibili cookies.
- Keep video and audio as `.part` files until FFmpeg succeeds.
- Use the user-provided NovaClip icon as the app brand asset. The unrelated reference extension icon is not redistributed.
- Ship unpackaged, self-contained x64 output to avoid certificate/MSIX friction for the first beta.
- Add both Inno Setup coverage updates and a portable updater in beta.1 because the first testing cycle must be able to replace an existing build.
- The default update source remains GitHub Releases. Private repositories are suitable for owner testing but not for anonymous public distribution; a signed public manifest is a future release item.
