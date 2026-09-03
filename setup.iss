; U-Sidecar - Inno Setup installer script
;
; Lives at the repo root on main. .github/workflows/publish.yml runs
; `iscc.exe setup.iss` from here after the signed app build has already
; been unzipped into `bin\` (USidecar.exe, USidecar.exe.config, vdd.cmd,
; version -- see build.yml's "Stage build output" step for exactly what's
; in there), sitting alongside this script as siblings.
;
; Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php).

#define MyAppName "U-Sidecar"
; Keep in sync with Program.AppVersion in app/Program.cs on main -- nothing
; here reads it automatically, since this branch has no source tree.
#define MyAppVersion "1.0.2"
#define MyAppPublisher "chainsaid"
#define MyAppURL "https://github.com/chainsaid/U-Sidecar"
#define MyAppExeName "USidecar.exe"

[Setup]
; Stable per-app GUID, not the version -- do not regenerate this on every
; release, Windows uses it to recognize "this is the same app, upgrading"
; versus "installing something new" for Add/Remove Programs.
AppId={{6A0B6E0E-6A61-4E7B-9C1B-2E7F6E6C6A6E}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The app itself requires admin (app.manifest: requireAdministrator, needed
; to write the VDD driver's custom-mode registry preset) -- match that here
; so the installed shortcuts don't need their own separate elevation prompt
; layered on top of the one the app already triggers on launch.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=out
OutputBaseFilename=USidecar-{#MyAppVersion}-setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
;LicenseFile=

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startupicon"; Description: "Start {#MyAppName} automatically when Windows starts"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; bin\ (sibling of this script) is where publish.yml's SignPath step drops
; the signed build output (see that workflow's "Sign the build" step,
; output-artifact-directory: bin).
Source: "bin\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "-silent"; Tasks: startupicon

[Run]
; runascurrentuser: without it, Inno Setup launches this post-install step
; via plain CreateProcess, which can't honor USidecar.exe's own
; requireAdministrator manifest and fails with "CreateProcess failed; code
; 740" (ERROR_ELEVATION_REQUIRED). This flag routes the launch through
; ShellExecute instead, which does trigger the UAC prompt properly -- same
; as double-clicking the exe or a shortcut to it normally would.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
