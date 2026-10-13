; The Windows installer (Inno Setup 6.7 or newer). It installs for the current user only - no
; administrator rights - and gives the app what a folder of files cannot: shortcuts, an entry
; under "Installed apps" with an uninstaller, and a place in "Open with" and "Send to".
;
; Its pages: welcome, the licence, where to install, what to add (Start menu and desktop
; shortcuts, "Send to", "Open with" - each a choice), a summary, and at the end the offer to
; start the app. They carry the app's logo and follow Windows' light or dark mode.
;
; The app updates itself with it: it downloads the newer version's installer and runs it without
; its pages ("/SILENT /FROMAPP", with "/RELAUNCH" to be started again afterwards - see [Code]).
;
; Uninstalling takes away everything the installer and the app put on the computer: the program,
; the shortcuts, the registry entries, the engines the app downloaded, its settings (unless the
; user keeps them) and whatever it left in the temp folder.
;
; build\windows\installer.ps1 runs it, with the names from Directory.Build.props.

#ifndef Source
  #error Run build\windows\installer.ps1: it passes the published folder and the app's names.
#endif

; every file type the app reads (FormatRegistry.Inputs - a test keeps the two the same)
#define Types "jpg|jpeg|png|gif|webp|bmp|tif|tiff|heic|ico|svg|mp3|wav|flac|aac|ogg|m4a|wma|opus|mp4|mkv|avi|webm|mov|wmv|flv|ts|m4v|txt|pdf|doc|docx|md|log|rtf|html|htm|odt|epub|xlsx|xls|ods|csv|pptx|ppt|odp|odg|pub|zip|7z|rar|tar|gz|tgz|bz2"

#define Program ProgramName + ".exe"
; the same program for a terminal (see FcArrangeDownload in FileConverter.csproj)
#define Console ProgramName + "-cli.exe"
; the logo in the installer's sizes (build/MakeIcon.cs draws them), and the app's licence
#define Wizard AddBackslash(SourcePath) + "wizard\"
#define Licence AddBackslash(SourcePath) + "..\..\LICENSE"
#define OpenWith "Software\Classes\Applications\" + Program
; where the app keeps its settings and the engines it downloads (WindowsPlatform.DataDirectory)
#define Data "{localappdata}\" + ProgramName

[Setup]
; identifies the app to Windows across versions (FcWindowsAppId in Directory.Build.props): never change it
AppId={{{#AppId}}
AppName={#DisplayName}
AppVersion={#Version}
AppVerName={#DisplayName} {#Version}
AppPublisher={#Company}
AppCopyright={#Copyright}
AppPublisherURL={#Website}
AppSupportURL={#Website}/issues
AppUpdatesURL={#Website}/releases/latest
AppComments={#Summary}
VersionInfoVersion={#Version}
VersionInfoDescription={#DisplayName} Setup
VersionInfoProductName={#DisplayName}

; for the current user: %LOCALAPPDATA%\Programs\<name>
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#DisplayName}
DisableProgramGroupPage=yes
AlwaysShowDirOnReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; one installer at a time
SetupMutex={#ProgramName}.Setup
; a copy of the app that is open is closed before its files are replaced: the installer says so
; on a page of its own and waits for a yes; run without pages, it just does it
CloseApplications=yes
RestartApplications=no

OutputDir={#Output}
OutputBaseFilename={#ProgramName}-{#Version}-win-x64-setup
SetupIconFile={#Icon}
UninstallDisplayIcon={app}\{#Program}
UninstallDisplayName={#DisplayName}
; the look: light or dark as Windows is set, the app's logo in the corner of every page and
; beside the first and the last one. (Dark mode takes its pictures from directives of its own;
; left out, it shows Inno Setup's stock ones. The logo has no background, so it suits both.)
WizardStyle=modern dynamic
WizardSmallImageFile={#Wizard}corner-55.png,{#Wizard}corner-83.png,{#Wizard}corner-110.png,{#Wizard}corner-138.png
WizardSmallImageFileDynamicDark={#Wizard}corner-55.png,{#Wizard}corner-83.png,{#Wizard}corner-110.png,{#Wizard}corner-138.png
WizardImageFile={#Wizard}side-164.png,{#Wizard}side-246.png,{#Wizard}side-328.png
WizardImageFileDynamicDark={#Wizard}side-164.png,{#Wizard}side-246.png,{#Wizard}side-328.png
DisableWelcomePage=no
LicenseFile={#Licence}
Compression=lzma2/max
SolidCompression=yes
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; what a careful person wants to know before clicking Next
WelcomeLabel2=This installs [name/ver] for your Windows account. Administrator rights are not needed.%n%nNothing else comes with it: no other programs, nothing that runs in the background or starts with Windows. The next pages let you choose the folder and the shortcuts.%n%nIt can be removed completely at any time under Settings > Apps > Installed apps.

[Tasks]
Name: "startmenu"; Description: "Add to the &Start menu"; GroupDescription: "Shortcuts:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "sendto"; Description: "Add to the ""Se&nd to"" menu: select files in a folder, right-click, and send them here to convert"; GroupDescription: "Windows integration:"
Name: "openwith"; Description: "List under ""&Open with"" for the files it converts (no file type's default program is changed)"; GroupDescription: "Windows integration:"

[InstallDelete]
; an update leaves nothing of the version before it
Type: filesandordirs; Name: "{app}\app"
; nor a shortcut that was wanted last time and is not wanted now
Type: files; Name: "{autoprograms}\{#DisplayName}.lnk"; Tasks: not startmenu
Type: files; Name: "{autodesktop}\{#DisplayName}.lnk"; Tasks: not desktopicon
Type: files; Name: "{usersendto}\{#DisplayName}.lnk"; Tasks: not sendto

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#DisplayName}"; Filename: "{app}\{#Program}"; Comment: "{#Summary}"; Tasks: startmenu
Name: "{autodesktop}\{#DisplayName}"; Filename: "{app}\{#Program}"; Comment: "{#Summary}"; Tasks: desktopicon
Name: "{usersendto}\{#DisplayName}"; Filename: "{app}\{#Program}"; Comment: "{#Summary}"; Tasks: sendto

[Registry]
; "Open with": offered for the file types below, without becoming the default for any of them
Root: HKA; Subkey: "{#OpenWith}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#DisplayName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "{#OpenWith}\shell\open\command"; ValueType: string; ValueData: """{app}\{#Program}"" ""%1"""; Tasks: openwith
#define Rest Types
#sub OneType
Root: HKA; Subkey: "{#OpenWith}\SupportedTypes"; ValueType: string; ValueName: ".{#Copy(Rest, 1, Pos("|", Rest + "|") - 1)}"; ValueData: ""; Tasks: openwith
#endsub
#for {Rest = Types; Rest != ""; Rest = Copy(Rest, Pos("|", Rest + "|") + 1)} OneType
; found by name: Win+R, the Start menu's search
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#Program}"; ValueType: string; ValueData: "{app}\{#Program}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#Program}"; Description: "{cm:LaunchProgram,{#DisplayName}}"; Flags: nowait postinstall skipifsilent
; an update the app asked for from its window: the app comes back (and says that it was updated)
Filename: "{app}\{#Program}"; Flags: nowait; Check: Relaunch

[UninstallDelete]
; whatever lies in the program's own folder that the installer did not put there by name (an
; update through the .zip, say) - the folder around it only if it is then empty: someone may have
; installed into a folder with other things in it
Type: filesandordirs; Name: "{app}\app"
Type: dirifempty; Name: "{app}"
; work files of a conversion that ended without tidying up (they carry the app's name, see WorkFiles)
Type: filesandordirs; Name: "{%TEMP}\{#ProgramName}-*"
; a pin on the taskbar, which Windows keeps as a shortcut of its own
Type: files; Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\{#DisplayName}.lnk"

[Code]
function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

// "/RELAUNCH": start the app again when the installation is through.
function Relaunch(): Boolean;
begin
  Result := HasSwitch('/RELAUNCH');
end;

// Windows lets nobody open a program for writing while it runs - which tells whether the copy
// in this very folder is running, whatever else of the same name may be.
function InUse(const FileName: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  if not FileExists(FileName) then Exit;
  try
    Stream := TFileStream.Create(FileName, fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

function AppRunning(): Boolean;
begin
  Result := InUse(ExpandConstant('{app}\{#Program}')) or InUse(ExpandConstant('{app}\{#Console}'));
end;

function ClosedWithin(Seconds: Integer): Boolean;
var
  Tries: Integer;
begin
  Tries := Seconds * 4;
  while (Tries > 0) and AppRunning() do
  begin
    Sleep(250);
    Tries := Tries - 1;
  end;
  Result := not AppRunning();
end;

// "/FROMAPP": the app has started this installer to update itself and is closing. Nothing is
// touched before it has; if it does not, nothing is touched at all.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if HasSwitch('/FROMAPP') and not ClosedWithin(30) then
    Result := '{#DisplayName} is still running. Close it, then update again.';
end;

// "Open with" was wanted by an earlier installation and is not wanted now: take it out again.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('openwith') then
    RegDeleteKeyIncludingSubkeys(HKA, '{#OpenWith}');
end;

// ---------- uninstalling ----------

var
  KeepSettings: Boolean;

// A program that is running cannot be removed: its files would stay behind.
function InitializeUninstall(): Boolean;
var
  ExitCode: Integer;
begin
  Result := True;
  while AppRunning() do
  begin
    if UninstallSilent then
    begin
      // nobody there to close it
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM "{#Program}"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM "{#Console}"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
      Result := ClosedWithin(10);
      Exit;
    end;
    if MsgBox('{#DisplayName} is still open.' + #13#10#13#10 + 'Close it to go on with uninstalling.',
        mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// While it reads a PDF through Word, the app switches off a question Word would put on a window
// nobody sees, and notes in its data folder what the setting was. It puts it back when it is
// done; after a crash the note is still there, and this does it instead.
procedure RestoreWordSetting(const Folder: String);
var
  Note, Key, CurVer: String;
  Previous: AnsiString;
  Dot: Integer;
begin
  Note := Folder + '\word-pdf-prompt.restore';
  if not FileExists(Note) or not LoadStringFromFile(Note, Previous) then Exit;
  // "Word.Application.16" -> the settings of Office 16.0
  if not RegQueryStringValue(HKCR, 'Word.Application\CurVer', '', CurVer) then Exit;
  Dot := Length(CurVer);
  while (Dot > 0) and (CurVer[Dot] <> '.') do Dot := Dot - 1;
  if Dot = 0 then Exit;
  Key := 'Software\Microsoft\Office\' + Copy(CurVer, Dot + 1, Length(CurVer)) + '.0\Word\Options';

  if StrToIntDef(Trim(String(Previous)), -1) >= 0 then
    RegWriteDWordValue(HKCU, Key, 'DisableConvertPdfWarning', StrToInt(Trim(String(Previous))))
  else
    RegDeleteValue(HKCU, Key, 'DisableConvertPdfWarning');
end;

// Settings, the engines the app downloaded (FFmpeg, 7-Zip), what it measured about this PC, an
// update it fetched: all in one folder, and all removed - but for the settings, if they are kept.
procedure RemoveData();
var
  Folder: String;
  Found: TFindRec;
begin
  Folder := ExpandConstant('{#Data}');
  if not DirExists(Folder) then Exit;
  RestoreWordSetting(Folder);

  if not KeepSettings then
  begin
    DelTree(Folder, True, True, True);
    Exit;
  end;
  if FindFirst(Folder + '\*', Found) then
  try
    repeat
      if (Found.Name <> '.') and (Found.Name <> '..') and (CompareText(Found.Name, 'settings.json') <> 0) then
      begin
        if Found.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          DelTree(Folder + '\' + Found.Name, True, True, True)
        else
          DeleteFile(Folder + '\' + Found.Name);
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Asked once the user has said yes to uninstalling, before anything is removed. Run without
    // questions, everything goes - unless "/KEEPSETTINGS" is given.
    KeepSettings := HasSwitch('/KEEPSETTINGS');
    if not UninstallSilent and FileExists(ExpandConstant('{#Data}\settings.json')) then
      KeepSettings := TaskDialogMsgBox('Remove your settings as well?',
        'The app, its shortcuts and the conversion engines it downloaded (FFmpeg, 7-Zip) are removed either way.' + #13#10#13#10 +
        'Your settings and saved presets can stay on this PC for a later installation.',
        mbConfirmation, MB_YESNO, ['Remove everything', 'Keep my settings'], 0) = IDNO;
  end
  else if CurUninstallStep = usPostUninstall then
    RemoveData();
end;
