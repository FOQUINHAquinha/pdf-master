; Instalador do PDF Master (Inno Setup 6 — licença própria, uso comercial permitido).
; Compilação: iscc /DAppVersion=0.8.0 /DSourceExe=..\publish\PDFMaster.exe /DOutputDir=..\installer-out PDFMaster.iss
;
; Instala só para o usuário atual por padrão (sem administrador); a pessoa pode escolher instalar
; para todos. O programa é um executável único que já traz OCR (Tesseract), FFmpeg e PDFium.
; LibreOffice e veraPDF são opcionais, instalados pela própria pessoa (não são distribuídos aqui).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\PDFMaster.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\installer-out"
#endif

[Setup]
AppId={{6D3A1F52-8C4B-4E7A-9B1D-2F5C7A9E3B41}
AppName=PDF Master
AppVersion={#AppVersion}
AppVerName=PDF Master {#AppVersion}
AppPublisher=Gustavo Farias Medeiros
AppComments=Suíte de ferramentas PDF 100% local: sem conta, sem anúncios, sem envio de arquivos.
DefaultDirName={autopf}\PDF Master
DefaultGroupName=PDF Master
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=PDFMaster-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=PDF Master
UninstallDisplayIcon={app}\PDFMaster.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
ChangesAssociations=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked
Name: "contextmenu"; Description: "Itens do PDF Master no menu de contexto dos PDFs (Mesclar, Comprimir, Converter, Extrair, Proteger, Abrir)"; GroupDescription: "Integração com o Windows (só para o seu usuário):"
Name: "openwith"; Description: "Mostrar o PDF Master em ""Abrir com"" para arquivos PDF"; GroupDescription: "Integração com o Windows (só para o seu usuário):"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "PDFMaster.exe"; Flags: ignoreversion
Source: "..\LEIA-ME-INSTALACAO.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{autoprograms}\PDF Master"; Filename: "{app}\PDFMaster.exe"
Name: "{autodesktop}\PDF Master"; Filename: "{app}\PDFMaster.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PDFMaster.exe"; Parameters: "--register-menu"; Tasks: contextmenu; Flags: runhidden waituntilterminated
Filename: "{app}\PDFMaster.exe"; Parameters: "--register-openwith"; Tasks: openwith; Flags: runhidden waituntilterminated
Filename: "{app}\PDFMaster.exe"; Description: "Abrir o PDF Master agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove as integrações com o Windows e os temporários do programa. Documentos nunca são tocados.
Filename: "{app}\PDFMaster.exe"; Parameters: "--unregister-all"; Flags: runhidden waituntilterminated; RunOnceId: "RemoverIntegracoes"
Filename: "{app}\PDFMaster.exe"; Parameters: "--clean-temp"; Flags: runhidden waituntilterminated; RunOnceId: "RemoverTemporarios"

[Code]
var
  RemoveData: Boolean;

function DataDir(): String;
begin
  Result := ExpandConstant('{localappdata}\PDFMaster');
end;

function InitializeUninstall(): Boolean;
begin
  RemoveData := False;
  if (not UninstallSilent()) and DirExists(DataDir()) then
    RemoveData := MsgBox('Remover também as configurações, o histórico, os fluxos e as pastas monitoradas do PDF Master?' + #13#10 + #13#10 +
      'Pasta: ' + DataDir() + #13#10 + #13#10 +
      'Seus documentos PDF não são apagados em nenhum caso.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if UninstallSilent() and (ExpandConstant('{param:REMOVEDATA|0}') = '1') then
    RemoveData := True;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveData then
    DelTree(DataDir(), True, True, True);
end;
