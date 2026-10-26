; iLovePDF Tools - per-user installer (no administrator rights needed).
; Build with: ISCC.exe installer\PdfTools.iss   (after publishing to artifacts\publish, see build.ps1)

#define AppName "iLovePDF Tools"
#define AppVersion "1.0.0"

[Setup]
AppId={{6EA48FA8-45BB-4E1D-BAD1-CD45643BCD71}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Internal
DefaultDirName={localappdata}\Programs\iLovePDF Tools
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputDir=..\artifacts
OutputBaseFilename=iLovePDFTools-Setup-{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\PdfToolsSettings.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "verify-key.ps1"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\iLovePDF Tools Settings"; Filename: "{app}\PdfToolsSettings.exe"

; Right-click entries (classic menu = "Show more options" on Windows 11). Per-user, so no admin needed.
[Registry]
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\PdfTools.Compress"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Compress PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\PdfTools.Compress"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\PdfTools.Compress\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" compress ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\PdfTools.Merge"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Merge PDFs"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\PdfTools.Merge"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\PdfTools.Merge\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" merge ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.doc\shell\PdfTools.ToPdf"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Convert to PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.doc\shell\PdfTools.ToPdf"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.doc\shell\PdfTools.ToPdf\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" office2pdf ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.docx\shell\PdfTools.ToPdf"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Convert to PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.docx\shell\PdfTools.ToPdf"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.docx\shell\PdfTools.ToPdf\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" office2pdf ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.ppt\shell\PdfTools.ToPdf"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Convert to PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.ppt\shell\PdfTools.ToPdf"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.ppt\shell\PdfTools.ToPdf\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" office2pdf ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pptx\shell\PdfTools.ToPdf"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Convert to PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pptx\shell\PdfTools.ToPdf"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pptx\shell\PdfTools.ToPdf\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" office2pdf ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\PdfTools.ToPdf"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Convert to PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\PdfTools.ToPdf"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\PdfTools.ToPdf\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" img2pdf ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\PdfTools.Combine"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Combine into one PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\PdfTools.Combine"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\PdfTools.Combine\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" imgcombine ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\PdfTools.ToPdf"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Convert to PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\PdfTools.ToPdf"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\PdfTools.ToPdf\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" img2pdf ""%1"""
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\PdfTools.Combine"; ValueType: string; ValueName: ""; ValueData: "iLovePDF: Combine into one PDF"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\PdfTools.Combine"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\PdfToolsRunner.exe,0"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\PdfTools.Combine\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PdfToolsRunner.exe"" imgcombine ""%1"""

[Run]
Filename: "{app}\PdfToolsRunner.exe"; Parameters: "--init --key-file ""{tmp}\ilp_key.txt""{code:SkipValidationParam}"; Flags: runhidden waituntilterminated; Check: KeyWasEntered; StatusMsg: "Saving your API key..."
Filename: "{app}\PdfToolsSettings.exe"; Description: "Open iLovePDF Tools settings"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  KeyPage: TInputQueryWizardPage;
  KeyEntered: Boolean;
  KeyVerified: Boolean;

procedure RegisterClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://www.iloveapi.com/signup', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure InitializeWizard;
var
  Btn: TNewButton;
begin
  KeyPage := CreateInputQueryPage(wpSelectDir,
    'iLoveAPI key',
    'Connect iLovePDF Tools to your iLoveAPI account',
    'Paste the PUBLIC key of your iLoveAPI project (console > API Keys). ' +
    'No account yet? Create a free one first, add a project, then copy its public key. ' +
    'You can also skip this and add the key later in iLovePDF Tools Settings.');
  KeyPage.Add('Project public key:', False);

  Btn := TNewButton.Create(WizardForm);
  Btn.Parent := KeyPage.Surface;
  Btn.Caption := 'Create a free iLoveAPI account...';
  Btn.Left := 0;
  Btn.Top := KeyPage.Edits[0].Top + KeyPage.Edits[0].Height + ScaleY(18);
  Btn.Width := ScaleX(240);
  Btn.Height := ScaleY(26);
  Btn.OnClick := @RegisterClick;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Key, KeyFile: String;
  Code: Integer;
begin
  Result := True;
  if CurPageID <> KeyPage.ID then Exit;

  Key := Trim(KeyPage.Values[0]);
  KeyEntered := Key <> '';
  KeyVerified := False;

  if not KeyEntered then
  begin
    Result := MsgBox('No key entered. The right-click actions will not work until you add a key in iLovePDF Tools Settings.' + #13#10#13#10 + 'Continue without a key?',
      mbConfirmation, MB_YESNO) = IDYES;
    Exit;
  end;

  if (Pos(' ', Key) > 0) or (Pos('"', Key) > 0) then
  begin
    MsgBox('The key must not contain spaces or quotes. Please paste only the public key.', mbError, MB_OK);
    KeyEntered := False;
    Result := False;
    Exit;
  end;

  ExtractTemporaryFile('verify-key.ps1');
  KeyFile := ExpandConstant('{tmp}\ilp_key.txt');
  SaveStringToFile(KeyFile, Key, False);

  WizardForm.NextButton.Enabled := False;
  try
    if not Exec('powershell.exe',
         '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\verify-key.ps1') + '" -KeyFile "' + KeyFile + '"',
         '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Code := 3;
  finally
    WizardForm.NextButton.Enabled := True;
  end;

  if Code = 0 then
    KeyVerified := True
  else if Code = 2 then
  begin
    MsgBox('iLoveAPI rejected this key. Please check that you copied the project PUBLIC key.', mbError, MB_OK);
    DeleteFile(KeyFile);
    KeyEntered := False;
    Result := False;
  end
  else
  begin
    Result := MsgBox('Could not reach iLoveAPI to verify the key (no internet?).' + #13#10#13#10 +
      'Continue anyway? The key will be saved without verification.', mbConfirmation, MB_YESNO) = IDYES;
    if not Result then
    begin
      DeleteFile(KeyFile);
      KeyEntered := False;
    end;
  end;
end;

function KeyWasEntered: Boolean;
begin
  Result := KeyEntered;
end;

function SkipValidationParam(Param: String): String;
begin
  if KeyVerified then Result := '' else Result := ' --skip-validation';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox('Also delete your saved API key, settings and activity log?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\PdfTools'), True, True, True);
end;
