; Inno Setup script for Network Share Quick Search (NSQS)
; Build with:  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\NSQS.iss
; Or run .\build.ps1 from the repo root (publish, then this script).
; Expects a framework-dependent publish in ..\publish\NSQS.exe — see README.md.

#define AppName        "Network Share Quick Search (NSQS)"
#ifndef AppVersion
  #define AppVersion   "1.2.4"
#endif
#define AppPublisher   "Adem Cifcioglu"
#define AppExeName     "NSQS.exe"
#define DotNetUrl      "https://dotnet.microsoft.com/download/dotnet/8.0"

[Setup]
AppId={{8E4C1A92-5F3B-4D7E-A1C6-9B2E8D4F0A71}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

PrivilegesRequired=lowest
DefaultDirName={autopf}\NSQS
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

ArchitecturesAllowed=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\Nsqs\app.ico

OutputDir=..\dist
OutputBaseFilename=NSQS-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function IsDotNet8DesktopInstalled(): Boolean;
var
  BasePath: String;
  FindRec: TFindRec;
begin
  Result := False;
  BasePath := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if not DirExists(BasePath) then
    Exit;

  if FindFirst(BasePath + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExeName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if IsDotNet8DesktopInstalled() then
    Exit;

  if MsgBox('{#AppName} needs the .NET 8 Desktop Runtime, which does not appear to be installed.'#13#10#13#10 +
            'Open the download page now? (Setup will close - run it again after installing the runtime.)'#13#10#13#10 +
            'Choose No to install anyway.',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', '{#DotNetUrl}', '', '', SW_SHOW, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp();
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'NSQS');
  end;
end;
