# Status — 1.0.0-beta.3

## Real-device startup hotfix

A Windows real-device beta.2 log proved that settings, SQLite and task restoration all completed successfully, then MainWindow.InitializeComponent failed with Microsoft.UI.Xaml.Markup.XamlParseException.

Beta.3 makes MainWindow XAML intentionally minimal and constructs NavigationView, menu items and Frame in C# after the Window component loads. It also catches individual page navigation failures and renders the exception inside the main window instead of making the entire application disappear.

## Installer migration

Beta.1/beta.2 installed executable files directly into %LocalAppData%\NovaClip, the same directory used for persistent data. That also made coverage installs vulnerable to stale binary/XAML resources.

Beta.3 installs binaries into:

%LocalAppData%\NovaClip\App

while settings, SQLite, logs and WebView2 profile remain under:

%LocalAppData%\NovaClip

The AppId is unchanged and UsePreviousAppDir is disabled so running the beta.3 installer over beta.2 migrates the active install path without requiring a manual uninstall.

## CI correction

The beta.2 smoke test only checked that the process stayed alive. A startup-failure window also keeps the process alive, so that test produced a false positive.

Beta.3 requires startup.log to prove both:
- Main window activated and startup completed.
- Navigation completed: BrowserPage.

It also fails on a recorded startup failure.
