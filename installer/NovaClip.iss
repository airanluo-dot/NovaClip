#ifndef PublishDir
#define PublishDir "publish\\win-x64"
#endif
#ifndef OutputDir
#define OutputDir "artifacts\\windows"
#endif

#define ProductName "NovaClip"
#define ProductVersion "1.0.0.2"
#define DisplayVersion "1.0.0-beta.2"
#define ProductPublisher "Aren Vox"
#define ProductExe "NovaClip.exe"

[Setup]
AppId={{BCF1B1C6-7D05-4E41-8A51-1B401ECF07E1}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppVerName={#ProductName} {#DisplayVersion}
AppPublisher={#ProductPublisher}
DefaultDirName={localappdata}\NovaClip
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir={#OutputDir}
OutputBaseFilename=NovaClip-1.0.0-beta.2-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\icons\NovaClip.ico
UninstallDisplayIcon={app}\{#ProductExe}
VersionInfoVersion={#ProductVersion}
VersionInfoProductVersion={#ProductVersion}
VersionInfoDescription=NovaClip Bilibili native Windows download manager

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.marker"

[Icons]
Name: "{autoprograms}\NovaClip"; Filename: "{app}\{#ProductExe}"
Name: "{autodesktop}\NovaClip"; Filename: "{app}\{#ProductExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\{#ProductExe}"; Description: "Launch NovaClip"; Flags: nowait postinstall skipifsilent
