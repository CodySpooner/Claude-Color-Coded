; Builds the installer. Run build.cmd first, then:  ISCC packaging\claude-color-coded.iss
; The version comes from the program itself, so it only needs changing in claude-color-coded.cs.

#define Version GetStringFileInfo(AddBackslash(SourcePath) + "..\ClaudeColorCoded.exe", "ProductVersion")

[Setup]
AppId=ClaudeColorCoded
AppName=Claude Color Coded
AppVersion={#Version}
AppPublisher=Xody
AppPublisherURL=https://github.com/CodySpooner/Claude-Color-Coded
DefaultDirName={autopf}\Claude Color Coded
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ClaudeColorCoded-{#Version}-setup
UninstallDisplayName=Claude Color Coded
UninstallDisplayIcon={app}\ClaudeColorCoded.exe

[Files]
Source: "..\ClaudeColorCoded.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Claude Color Coded"; Filename: "{app}\ClaudeColorCoded.exe"
Name: "{userstartup}\Claude Color Coded"; Filename: "{app}\ClaudeColorCoded.exe"

[InstallDelete]
; The name Windows gives a shortcut made by hand, as the earlier setup steps asked for. It would keep starting that older copy.
Type: files; Name: "{userstartup}\ClaudeColorCoded.exe - Shortcut.lnk"

[Run]
Filename: "{app}\ClaudeColorCoded.exe"; Flags: nowait

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im ClaudeColorCoded.exe"; Flags: runhidden; RunOnceId: "StopProgram"

[Code]
// A running copy locks its file, so an update has to stop it first.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ClaudeColorCoded.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
