Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"

!ifndef APP_PAYLOAD
  !error "Provide /DAPP_PAYLOAD=<built x86 application directory>."
!endif
!ifndef DOTNET_REDIST
  !error "Provide /DDOTNET_REDIST=<official Microsoft .NET Framework 4.8 offline exe>."
!endif
!ifndef OUTPUT_EXE
  !error "Provide /DOUTPUT_EXE=<setup exe>."
!endif

!define APP_VERSION "0.15.0"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\OfflineStore-Machine"

Name "OfflineStore - Sales and Inventory"
Icon "..\src\OfflineStore\Assets\OfflineStore.ico"
UninstallIcon "..\src\OfflineStore\Assets\OfflineStore.ico"
OutFile "${OUTPUT_EXE}"
RequestExecutionLevel admin
InstallDir "$PROGRAMFILES\OfflineStore"
InstallDirRegKey HKLM "${UNINSTALL_KEY}" "InstallLocation"
SetCompressor /SOLID lzma
!include "InstallPathSafety.nsh"
ShowInstDetails show
ShowUninstDetails show
BrandingText "OfflineStore | Windows offline installation"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\OfflineStore.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch OfflineStore"
!insertmacro MUI_PAGE_WELCOME
!define MUI_DIRECTORYPAGE_TEXT_TOP "Choose a dedicated empty folder or an existing OfflineStore installation. Do not select a drive root or a business-data folder."
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ; Windows 7 SP1 / 8.1 / compatible Windows 10/11 builds.
  ; The signed Microsoft prerequisite is embedded in this one Setup.exe.
  SetRegView 32
  ClearErrors
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  IfErrors InstallDotNet
  IntCmp $0 528040 DotNetReady InstallDotNet DotNetReady

  InstallDotNet:
    MessageBox MB_YESNO|MB_ICONQUESTION "OfflineStore requires Microsoft .NET Framework 4.8. Its official offline installer is included. Install it now?" IDYES DoInstall
    Abort
  DoInstall:
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=dotnet48-offline.exe "${DOTNET_REDIST}"
    DetailPrint "Installing signed Microsoft .NET Framework 4.8 offline prerequisite..."
    ExecWait '"$PLUGINSDIR\dotnet48-offline.exe" /passive /norestart' $1
    ${If} $1 == 3010
      SetRebootFlag true
    ${ElseIf} $1 != 0
      MessageBox MB_ICONSTOP|MB_OK "Installation cannot continue. Check the required .NET Framework 4.8 runtime and retry."
      Abort
    ${EndIf}

    ; Do not proceed with a runtime below the required version.
    ClearErrors
    ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
    IfErrors NeedReboot
    IntCmp $0 528040 DotNetReady NeedReboot DotNetReady

  NeedReboot:
    MessageBox MB_ICONINFORMATION|MB_OK "Windows needs to restart to finish installing .NET Framework 4.8. Restart and run OfflineStore Setup again."
    Abort

  DotNetReady:
    Return
FunctionEnd

Section "OfflineStore" Main
  Call ValidateInstallDirectory
  SetShellVarContext all
  SetOutPath "$INSTDIR"
  File /r /x *.pdb "${APP_PAYLOAD}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "OfflineStore - Sales and Inventory"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "OfflineStore"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  CreateDirectory "$SMPROGRAMS\OfflineStore"
  CreateShortcut "$SMPROGRAMS\OfflineStore\OfflineStore.lnk" "$INSTDIR\OfflineStore.exe"
  CreateShortcut "$SMPROGRAMS\OfflineStore\إزالة التثبيت.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\OfflineStore.lnk" "$INSTDIR\OfflineStore.exe"
  ; Only private local subnets may reach the offline receipt image endpoint.
  ; The QR contains an unguessable HMAC token; no public/internet listener.
  nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="OfflineStore Receipt LAN"'
  Pop $0
  nsExec::ExecToLog 'netsh advfirewall firewall add rule name="OfflineStore Receipt LAN" dir=in action=allow program="$INSTDIR\OfflineStore.exe" enable=yes profile=private protocol=TCP localport=18765 remoteip=localsubnet'
  Pop $0
  ; Keep legacy per-user data at %LOCALAPPDATA%\OfflineStore\store.db.
  ; Do not move it to Program Files or wipe it when uninstalling.
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  nsExec::ExecToLog 'netsh advfirewall firewall delete rule name="OfflineStore Receipt LAN"'
  Pop $0
  Delete "$DESKTOP\OfflineStore.lnk"
  Delete "$SMPROGRAMS\OfflineStore\OfflineStore.lnk"
  Delete "$SMPROGRAMS\OfflineStore\إزالة التثبيت.lnk"
  RMDir "$SMPROGRAMS\OfflineStore"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  ; Only application binaries are removed; business data stays.
SectionEnd
