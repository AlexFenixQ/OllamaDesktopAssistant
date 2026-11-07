#ifndef PublishDir
  #error PublishDir must be supplied by Package.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by Package.ps1
#endif
[Setup]
AppId={{4959D6E5-8A31-45A2-B215-992E064C87B9}
AppName=Ollama Desktop
AppVersion=0.1.30
AppVerName=Ollama Desktop 0.1.30
AppPublisher=Ollama Desktop Project
DefaultDirName={localappdata}\Programs\OllamaDesktopAssistant
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Ollama Desktop
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.10240
AppMutex=Local\OllamaDesktopAssistant
OutputDir={#OutputDir}
OutputBaseFilename=OllamaDesktop-0.1.30-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\OllamaDesktop.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: checkedonce
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: ".build-owned,smoke-test.json"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{userprograms}\Ollama Desktop"; Filename: "{app}\OllamaDesktop.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Ollama Desktop"; Filename: "{app}\OllamaDesktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Run]
Filename: "{app}\OllamaDesktop.exe"; Description: "Открыть Ollama Desktop"; Flags: nowait postinstall skipifsilent
[Code]
function IsNonEmptyDirectory(const Dir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Dir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsHexToken(const Token: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  if Length(Token) <> 32 then Exit;
  for I := 1 to Length(Token) do
    if Pos(UpperCase(Copy(Token, I, 1)), '0123456789ABCDEF') = 0 then Exit;
  Result := True;
end;

function HasVerifiedUpgradePermit: Boolean;
var
  Token, PermitPath, Marker, Inventory: String;
  Lines: TArrayOfString;
begin
  Result := False;
  Token := ExpandConstant('{param:ODAVERIFY|}');
  if not IsHexToken(Token) then Exit;
  Marker := ExpandConstant('{app}\release-installed.json');
  Inventory := ExpandConstant('{app}\installed-files.json');
  if not FileExists(Marker) or not FileExists(Inventory) then Exit;
  PermitPath := ExpandConstant('{localappdata}\OllamaDesktopAssistant\UpgradePermits\') + Token + '.txt';
  if not LoadStringsFromFile(PermitPath, Lines) then Exit;
  if GetArrayLength(Lines) <> 5 then Exit;
  if Lines[0] <> 'OllamaDesktopAssistant' then Exit;
  if Lines[1] <> '0.1.30' then Exit;
  if Lines[2] <> Token then Exit;
  if UpperCase(Lines[3]) <> UpperCase(GetSHA256OfFile(Marker)) then Exit;
  if UpperCase(Lines[4]) <> UpperCase(GetSHA256OfFile(Inventory)) then Exit;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsNonEmptyDirectory(ExpandConstant('{app}')) and not HasVerifiedUpgradePermit then
    Result := 'Каталог установки уже содержит файлы. Для обновления известной версии запустите подготовленный сценарий обновления, который проверяет все файлы и создаёт резервную копию. Установка в неизвестную или изменённую папку запрещена.';
end;
