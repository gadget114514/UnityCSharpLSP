#define MyAppName "UnityCSharpLSP"
#define MyAppVersion "0.5.1"
#define MyAppPublisher "gadget114514"
#define MyAppURL "https://github.com/gadget114514/UnityCSharpLSP"
#define MyAppExeName "UnityCSharpLSP.Server.exe"
#define MyAppAliasExeName "UnityCSharpLSP.exe"
#define MyRefsExeName "lsp-refs.exe"

[Setup]
AppId={{E6F7A9C0-8B35-4F54-9E11-3D5F5A9A7B31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename=UnityCSharpLSP-v{#MyAppVersion}-Setup
OutputDir=artifacts
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "artifacts\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "artifacts\publish\{#MyRefsExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "addtopath"; Description: "Add application directory to User PATH environment variable"; GroupDescription: "Environment Settings:"; Flags: checkedonce

[Code]
const
    WM_SETTINGCHANGE = $001A;
    SMTO_ABORTIFHUNG = $0002;

function SendMessageTimeout(hWnd: HWND; Msg: Cardinal; wParam: LongInt; lParam: String; fuFlags: Cardinal; uTimeout: Cardinal; out lpdwResult: LongInt): LongInt;
external 'SendMessageTimeoutW@user32.dll stdcall';

function CreateHardLink(lpFileName, lpExistingFileName: String; lpSecurityAttributes: LongInt): Boolean;
external 'CreateHardLinkW@kernel32.dll stdcall';

procedure AddToPath();
var
    CurrentPath: String;
    AppDir: String;
    NewPath: String;
    ResultVal: LongInt;
begin
    AppDir := ExpandConstant('{app}');
    if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CurrentPath) then
        CurrentPath := '';

    if Pos(';' + Uppercase(AppDir) + ';', ';' + Uppercase(CurrentPath) + ';') = 0 then
    begin
        if (Length(CurrentPath) > 0) and (CurrentPath[Length(CurrentPath)] <> ';') then
            NewPath := CurrentPath + ';' + AppDir
        else
            NewPath := CurrentPath + AppDir;

        RegWriteStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', NewPath);
        SendMessageTimeout($FFFF, WM_SETTINGCHANGE, 0, 'Environment', SMTO_ABORTIFHUNG, 5000, ResultVal);
    end;
end;

procedure RemoveFromPath();
var
    CurrentPath: String;
    AppDir: String;
    P: Integer;
    ResultVal: LongInt;
begin
    AppDir := ExpandConstant('{app}');
    if RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CurrentPath) then
    begin
        P := Pos(Uppercase(AppDir), Uppercase(CurrentPath));
        if P > 0 then
        begin
            Delete(CurrentPath, P, Length(AppDir));
            while Pos(';;', CurrentPath) > 0 do
                StringChangeEx(CurrentPath, ';;', ';', True);

            if (Length(CurrentPath) > 0) and (CurrentPath[1] = ';') then
                Delete(CurrentPath, 1, 1);
            if (Length(CurrentPath) > 0) and (CurrentPath[Length(CurrentPath)] = ';') then
                Delete(CurrentPath, Length(CurrentPath), 1);

            RegWriteStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CurrentPath);
            SendMessageTimeout($FFFF, WM_SETTINGCHANGE, 0, 'Environment', SMTO_ABORTIFHUNG, 5000, ResultVal);
        end;
    end;
end;

procedure CreateExeAlias();
var
    TargetPath: String;
    AliasPath: String;
begin
    TargetPath := ExpandConstant('{app}\{#MyAppExeName}');
    AliasPath := ExpandConstant('{app}\{#MyAppAliasExeName}');
    if FileExists(TargetPath) then
    begin
        DeleteFile(AliasPath);
        if not CreateHardLink(AliasPath, TargetPath, 0) then
            CopyFile(TargetPath, AliasPath, False);
    end;
end;

procedure RemoveExeAlias();
var
    AliasPath: String;
begin
    AliasPath := ExpandConstant('{app}\{#MyAppAliasExeName}');
    if FileExists(AliasPath) then
        DeleteFile(AliasPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
    if CurStep = ssPostInstall then
    begin
        CreateExeAlias();
        if WizardIsTaskSelected('addtopath') then
            AddToPath();
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
    if CurUninstallStep = usPostUninstall then
    begin
        RemoveExeAlias();
        RemoveFromPath();
    end;
end;
