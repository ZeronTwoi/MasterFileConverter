; The Windows installer (Inno Setup 6 or newer). It installs for the current user only - no
; administrator rights - and gives the app what a folder of files cannot: a Start menu entry,
; an entry under "Installed apps" with an uninstaller, and a place in "Open with" and "Send to".
;
; build\windows\installer.ps1 runs it, with the names from Directory.Build.props.

#ifndef Source
  #error Run build\windows\installer.ps1: it passes the published folder and the app's names.
#endif

; every file type the app reads (FormatRegistry.Inputs - a test keeps the two the same)
#define Types "jpg|jpeg|png|gif|webp|bmp|tif|tiff|heic|ico|mp3|wav|flac|aac|ogg|m4a|wma|opus|mp4|mkv|avi|webm|mov|wmv|flv|ts|m4v|txt|pdf|doc|docx|md|log|zip|7z|rar|tar|gz|tgz|bz2"

#define Program ProgramName + ".exe"
#define OpenWith "Software\Classes\Applications\" + Program

[Setup]
; identifies the app to Windows across versions: never change it
AppId={{6F0D6A52-3C1B-4E57-9B4E-5B8A2D7C91E4}
AppName={#DisplayName}
AppVersion={#Version}
AppVerName={#DisplayName} {#Version}
AppPublisher={#Company}
AppCopyright={#Copyright}
AppPublisherURL={#Website}
AppSupportURL={#Website}/issues
AppUpdatesURL={#Website}/releases/latest
VersionInfoVersion={#Version}
VersionInfoDescription={#DisplayName} Setup
VersionInfoProductName={#DisplayName}

; for the current user: %LOCALAPPDATA%\Programs\<name>
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#DisplayName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#Output}
OutputBaseFilename={#ProgramName}-{#Version}-win-x64-setup
SetupIconFile={#Icon}
UninstallDisplayIcon={app}\{#Program}
UninstallDisplayName={#DisplayName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "sendto"; Description: "Add to the ""Send to"" menu: select files in a folder, right-click, and send them here to convert"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[InstallDelete]
; an update leaves nothing of the version before it
Type: filesandordirs; Name: "{app}\app"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#DisplayName}"; Filename: "{app}\{#Program}"; Comment: "{#Summary}"
Name: "{autodesktop}\{#DisplayName}"; Filename: "{app}\{#Program}"; Comment: "{#Summary}"; Tasks: desktopicon
Name: "{usersendto}\{#DisplayName}"; Filename: "{app}\{#Program}"; Comment: "{#Summary}"; Tasks: sendto

[Registry]
; "Open with": offered for the file types below, without becoming the default for any of them
Root: HKA; Subkey: "{#OpenWith}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#DisplayName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "{#OpenWith}\shell\open\command"; ValueType: string; ValueData: """{app}\{#Program}"" ""%1"""
#define Rest Types
#sub OneType
Root: HKA; Subkey: "{#OpenWith}\SupportedTypes"; ValueType: string; ValueName: ".{#Copy(Rest, 1, Pos("|", Rest + "|") - 1)}"; ValueData: ""
#endsub
#for {Rest = Types; Rest != ""; Rest = Copy(Rest, Pos("|", Rest + "|") + 1)} OneType
; found by name: Win+R, the Start menu's search
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#Program}"; ValueType: string; ValueData: "{app}\{#Program}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#Program}"; Description: "{cm:LaunchProgram,{#DisplayName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Settings and the downloaded engines are kept in a folder of their own, so that an update or a
// reinstall finds them again. An uninstall asks; one run without questions keeps them.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then Exit;
  Data := ExpandConstant('{localappdata}\{#ProgramName}');
  if not DirExists(Data) then Exit;
  if MsgBox('Also remove your settings and the conversion engines the app downloaded (FFmpeg, 7-Zip)?'
      + #13#10#13#10 + 'Choose No to keep them for a later installation.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(Data, True, True, True);
end;
