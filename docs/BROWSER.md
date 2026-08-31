# Browser

WebView2 is only the Bilibili content region. NovaClip owns the native toolbar, navigation state, policy, status and recovery UI.

`NewWindowRequested` is always marked handled. Trusted Bilibili HTTP(S) URLs navigate in the current view; external HTTP(S) URLs produce an InfoBar action; dangerous and unknown schemes are blocked. Navigation, source, history, title, completion and process-failure events are all observed. The profile lives in `%LocalAppData%\NovaClip\WebView2` and is not shared with Edge or Chrome.

The browser uses a navigation generation. Media responses captured for an older generation are discarded.
