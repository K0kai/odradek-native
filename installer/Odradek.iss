; Instalador do Odradek (Inno Setup 6). Instala só no usuário atual, sem pedir administrador.
;
;   iscc /DAppVersion=2026.1125.1800 installer\Odradek.iss
;   iscc /DAppVersion=... /DSourceDir=<pasta publicada> /DOutputDir=<saída> /DFeedUrl=<feed> installer\Odradek.iss
;
; A pasta publicada vem do build.ps1 (self-contained: o instalador leva o .NET junto). Os caminhos relativos partem da pasta deste arquivo.

#ifndef AppVersion
  #error Informe a versão: iscc /DAppVersion=1.2.3 installer\Odradek.iss
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Odradek"
#endif
#ifndef OutputDir
  #define OutputDir "..\out"
#endif
; De onde o app instalado se atualiza sozinho (ver Updater.cs).
#ifndef FeedUrl
  #define FeedUrl "https://github.com/K0kai/odradek-native/releases/latest/download/"
#endif

[Setup]
; O AppId nunca muda (o app o usa em InstalledApp.cs para corrigir a versão exibida depois de se atualizar).
AppId={{8C2154E8-EA11-46CA-A9BA-7C18EF498510}
AppName=Odradek
AppVersion={#AppVersion}
AppVerName=Odradek {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=K0kai
AppSupportURL=https://github.com/K0kai/odradek-native/issues
PrivilegesRequired=lowest
; A mesma pasta do install.ps1: quem instalou por ele é atualizado no lugar. Fixa, porque a desinstalação apaga a pasta inteira.
DefaultDirName={localappdata}\Programs\Odradek
DisableDirPage=yes
DisableProgramGroupPage=yes
; Fora da pasta do app: a atualização automática troca a pasta inteira e levaria o desinstalador junto.
UninstallFilesDir={localappdata}\Programs\Odradek-uninstall
UninstallDisplayName=Odradek
UninstallDisplayIcon={app}\Odradek.exe
; O mutex de instância única do app (Program.cs): com o Odradek aberto, o instalador pede para fechá-lo.
AppMutex=Odradek.Instance
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\Odradek.App\Assets\favicon.ico
OutputDir={#OutputDir}
OutputBaseFilename=Odradek-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Odradek"; Filename: "{app}\Odradek.exe"; WorkingDir: "{app}"; Comment: "Odradek: coleta, triagem e candidaturas"
Name: "{userdesktop}\Odradek"; Filename: "{app}\Odradek.exe"; WorkingDir: "{app}"; Comment: "Odradek: coleta, triagem e candidaturas"; Tasks: desktopicon

[Run]
; Marcada, abre o app direto no passo a passo da extensão; a segunda opção então não precisa abrir outra vez (o app é de instância única).
Filename: "{app}\Odradek.exe"; Parameters: "--extensao"; Description: "Instalar a extensão do navegador"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Odradek.exe"; Description: "{cm:LaunchProgram,Odradek}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function DataDir(): String;
begin
  Result := GetEnv('ODRADEK_DATA_DIR');
  if Result = '' then
    Result := ExpandConstant('{localappdata}\Odradek');
end;

{ Pastas que a troca da atualização automática deixa ao lado da instalação. }
procedure RemoveUpdateLeftovers();
begin
  DelTree(ExpandConstant('{app}') + '.staging', True, True, True);
  DelTree(ExpandConstant('{app}') + '.old', True, True, True);
  DelTree(ExpandConstant('{app}') + '.bad', True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { Uma versão preparada antes não vale para esta instalação, e o que falhou antes pode ser tentado de novo. }
    RemoveUpdateLeftovers();
    DeleteFile(DataDir() + '\updates\staged.json');
    DeleteFile(DataDir() + '\updates\failed.txt');
  end;
  { Liga a atualização automática: o app só se atualiza se achar este arquivo ao lado do Odradek.exe. }
  if CurStep = ssPostInstall then
    if not SaveStringToFile(ExpandConstant('{app}\update-source.txt'), '{#FeedUrl}', False) then
      SuppressibleMsgBox('Não consegui ligar a atualização automática (update-source.txt).', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveUpdateLeftovers();
    { Depois de uma atualização automática há arquivos que o instalador não conhece. Só apaga a pasta que é mesmo a do app. }
    if CompareText(ExtractFileName(ExpandConstant('{app}')), 'Odradek') = 0 then
      DelTree(ExpandConstant('{app}'), True, True, True);
    SuppressibleMsgBox('Os seus dados continuam em ' + DataDir() + '.', mbInformation, MB_OK, IDOK);
  end;
end;
