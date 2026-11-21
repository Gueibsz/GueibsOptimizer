; Instalador do Gueibs Optimizer (Inno Setup 6).
; Compile com tools\build-installer.ps1, que publica o exe e chama:
;   iscc /DAppVersion=<versão> /DSourceExe=..\dist\GueibsOptimizer.exe installer\GueibsOptimizer.iss
; Saída: dist\GueibsOptimizer-Setup-<versão>.exe

#ifndef AppVersion
  #define AppVersion "2.5"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\GueibsOptimizer.exe"
#endif

#define AppName "Gueibs Optimizer"
#define AppExe "GueibsOptimizer.exe"

[Setup]
; AppId fixo: identifica o app para atualizações e para o desinstalador. Não altere.
AppId={{7C1E2B54-3F8A-4D6B-9E21-5A0C8D4F6B13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Gueibs Optimizer
AppComments=Otimizador de PC gamer para Windows 10/11 (uso pessoal)
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=GueibsOptimizer-Setup-{#AppVersion}
SetupIconFile=..\src\GueibsOptimizer\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Fecha o app aberto antes de atualizar/desinstalar (mesmo mutex de instância única do app).
AppMutex=Local\GueibsOptimizer.SingleInstance
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador do {#AppName}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
; Os dois atalhos são opcionais (vêm marcados; a pessoa desmarca o que não quiser).
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"
Name: "startmenuicon"; Description: "Criar atalho no menu Iniciar"; GroupDescription: "Atalhos:"
Name: "defender"; Description: "Adicionar SOMENTE a pasta de instalação às exclusões do Microsoft Defender (evita lentidão na leitura de sensores; o Defender continua protegendo o resto do PC)"; GroupDescription: "Opções:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\package\LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\package\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Sem atalho de "modo simulação" na versão para distribuir (o --dry-run continua disponível pela linha de comando).
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenuicon
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Quem atualiza de versões antigas: apaga a tarefa de início com o Windows (o app só abre manualmente, sempre como
; administrador), leva a revisão semanal e o plano de energia para os nomes novos.
Filename: "{app}\{#AppExe}"; Parameters: "--migrate-legacy"; Flags: runhidden waituntilterminated; StatusMsg: "Atualizando itens da versão anterior..."
; A opção marcada é executada pelo próprio app (mesmo código usado na tela do Windows).
Filename: "{app}\{#AppExe}"; Parameters: "--defender-add"; Tasks: defender; Flags: runhidden waituntilterminated; StatusMsg: "Adicionando a exclusão do Defender..."
; Componentes do app, SEMPRE instalados (sem opção para desmarcar): driver PawnIO pelo winget (namazso.PawnIO) e Intel PresentMon
; da página oficial da Intel com assinatura verificada. Precisa de internet; se falhar, o app continua instalado e oferece de novo na tela.
Filename: "{app}\{#AppExe}"; Parameters: "--install-components pawnio"; Flags: runhidden waituntilterminated; StatusMsg: "Instalando o driver PawnIO (sensores da CPU)..."
Filename: "{app}\{#AppExe}"; Parameters: "--install-components presentmon"; Flags: runhidden waituntilterminated; StatusMsg: "Baixando o Intel PresentMon (contador de FPS)..."
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName}"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
; Remove a tarefa de início automático, a revisão semanal e a exclusão do Defender (se existirem), e os componentes
; instalados junto com o app: o PresentMon baixado pelo app e o driver PawnIO (só se foi o app que o instalou).
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "GueibsOptimizerCleanup"; StatusMsg: "Removendo o PawnIO e o PresentMon instalados com o app..."

[InstallDelete]
; Versões até a 2.0 se chamavam GameOptimizer: remove o exe e os atalhos antigos ao atualizar.
Type: files; Name: "{app}\GameOptimizer.exe"
Type: files; Name: "{autodesktop}\GameOptimizer.lnk"
Type: filesandordirs; Name: "{autoprograms}\GameOptimizer"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  ComponentsNote = 'Componentes baixados automaticamente durante a instalação (precisa de internet):' + #13#10 +
    '      Driver PawnIO (winget namazso.PawnIO): temperatura, clock e potência da CPU' + #13#10 +
    '      Intel PresentMon (site oficial da Intel, assinatura verificada): contador de FPS';
  // Mesmo AppId de todas as versões (inclusive as antigas, chamadas GameOptimizer): é por ele que o instalador acha a versão instalada.
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C1E2B54-3F8A-4D6B-9E21-5A0C8D4F6B13}_is1';

// Versão já instalada ('' se não há nenhuma). Instalar a versão nova por cima atualiza no mesmo lugar e mantém os dados.
function InstalledVersion(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result) then
      Result := '';
end;

function UpgradeNote(): String;
var
  Old: String;
begin
  Result := '';
  Old := InstalledVersion();
  if Old = '' then
    Exit;
  if Old = '{#AppVersion}' then
    Result := 'Reinstalação: a versão {#AppVersion} já está instalada e será reinstalada no mesmo lugar.'
  else
    Result := 'Atualização: a versão ' + Old + ' instalada será atualizada para a {#AppVersion}.';
  Result := Result + #13#10 + '      Configurações, perfis, jogos e o histórico da Restauração são mantidos; não é preciso desinstalar antes.';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if UpgradeNote() <> '' then
    Result := UpgradeNote() + NewLine + NewLine;
  Result := Result + MemoDirInfo + NewLine + NewLine;
  if MemoGroupInfo <> '' then
    Result := Result + MemoGroupInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result + ComponentsNote;
end;
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not UninstallSilent() then
    Result := MsgBox('Os ajustes do Windows feitos pelo Gueibs Optimizer NÃO são desfeitos pela desinstalação.' + #13#10 + #13#10 +
      'Se quiser voltar tudo ao original, cancele, abra o app e use Restauração > Desfazer tudo antes de desinstalar.' + #13#10 + #13#10 +
      'Os componentes instalados junto com o app também serão removidos: o Intel PresentMon e o driver PawnIO ' +
      '(o PawnIO só se foi instalado pelo Gueibs Optimizer; se outro programa o instalou antes, ele fica).' + #13#10 + #13#10 +
      'Continuar com a desinstalação?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  DataDir := ExpandConstant('{userappdata}\GueibsOptimizer');
  if not DirExists(DataDir) then
    Exit;
  // Em modo silencioso os dados são mantidos (opção mais segura: o backup permite desfazer ajustes depois).
  if UninstallSilent() then
    Exit;
  if MsgBox('Manter suas configurações, perfis e o backup dos valores originais em:' + #13#10 + DataDir + '?' + #13#10 + #13#10 +
    'Recomendado: Sim. Sem o backup não é possível desfazer, pelo app, os ajustes que ainda estiverem aplicados.',
    mbConfirmation, MB_YESNO) = IDNO then
    DelTree(DataDir, True, True, True);
end;
