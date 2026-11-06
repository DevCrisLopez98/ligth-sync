#define AppName "LightSync"
#define AppVersion "0.1.0"
#define AppPublisher "LightSync"
#define AppExe "LightSync.exe"

[Setup]
AppId={{A7B3E4C1-6F2D-4A18-9C55-1D8E4B7A0F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=LightSync-Setup
SetupIconFile=..\src\LightSync\Assets\LightSync.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos:"; Flags: checkedonce
Name: "taskbar"; Description: "Anclar a la barra de tareas"; GroupDescription: "Accesos:"; Flags: unchecked
Name: "startup"; Description: "Ejecutar al iniciar Windows, minimizado en la barra"; GroupDescription: "Inicio:"; Flags: unchecked

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\OpenRGB\*"; DestDir: "{app}\OpenRGB"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "OPENRGB-NOTICE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "redist\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; IconFilename: "{app}\{#AppExe}"

[Run]
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"; StatusMsg: "Instalando el driver de hardware..."; Check: NeedsPawnIO and PawnSetupExists; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Abrir LightSync"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""LightSync"""; Flags: runhidden; RunOnceId: "RemoveStartup"

[Code]
function NeedsPawnIO: Boolean;
begin
  Result := not DirExists(ExpandConstant('{pf}\PawnIO'));
end;

function PawnSetupExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\PawnIO_setup.exe'));
end;

procedure PinToTaskbar;
var
  ResultCode: Integer;
begin
  if not WizardIsTaskSelected('taskbar') then
    Exit;
  Exec('powershell.exe',
    '-NoProfile -Command "$s = New-Object -ComObject Shell.Application; $f = $s.NameSpace(''' + ExpandConstant('{group}') + '''); if ($f) { $i = $f.ParseName(''LightSync.lnk''); if ($i) { $i.InvokeVerb(''taskbarpin'') } }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CreateStartupTask;
var
  ResultCode: Integer;
  Command: String;
begin
  if not WizardIsTaskSelected('startup') then
    Exit;
  Command := '/Create /F /TN "LightSync" /SC ONLOGON /RL HIGHEST /TR "\"' + ExpandConstant('{app}\{#AppExe}') + '\" --tray"';
  Exec(ExpandConstant('{sys}\schtasks.exe'), Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    CreateStartupTask;
    PinToTaskbar;
  end;
end;
