Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"

!ifndef APP_PAYLOAD
  !error "Provide /DAPP_PAYLOAD=<absolute path of built application folder>."
!endif
!ifndef OUTPUT_EXE
  !error "Provide /DOUTPUT_EXE=<absolute path to setup.exe>."
!endif

!define APP_NAME "OfflineStore"
!define APP_VERSION "0.15.0"
!define APP_REGKEY "Software\OfflineStore"
!define APP_UNINSTALL "Software\Microsoft\Windows\CurrentVersion\Uninstall\OfflineStore"

Name "${APP_NAME} - Sales and Inventory"
Icon "..\src\OfflineStore\Assets\OfflineStore.ico"
UninstallIcon "..\src\OfflineStore\Assets\OfflineStore.ico"
OutFile "${OUTPUT_EXE}"
InstallDir "$LOCALAPPDATA\Programs\OfflineStore"
InstallDirRegKey HKCU "${APP_REGKEY}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
!include "InstallPathSafety.nsh"
ShowInstDetails show
ShowUninstDetails show
BrandingText "OfflineStore | Review build — not commercial release"

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
  ; .NET Framework 4.8: minimum Release value = 528040, see Microsoft documentation.
  SetRegView 32
  ClearErrors
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  IfErrors missingDotNet
  IntCmp $0 528040 netOk missingDotNet netOk
  missingDotNet:
    MessageBox MB_ICONSTOP|MB_OK "Installation cannot continue. Check the required .NET Framework 4.8 runtime and retry."
    Abort
  netOk:
    Return
FunctionEnd

Section "OfflineStore" Main
  Call ValidateInstallDirectory
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  ; All compiled x86 build output and required native sqlite3.dll are packaged.
  File /r /x *.pdb "${APP_PAYLOAD}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APP_REGKEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${APP_UNINSTALL}" "DisplayName" "OfflineStore - Sales and Inventory"
  WriteRegStr HKCU "${APP_UNINSTALL}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${APP_UNINSTALL}" "Publisher" "OfflineStore"
  WriteRegStr HKCU "${APP_UNINSTALL}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${APP_UNINSTALL}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "${APP_UNINSTALL}" "NoModify" 1
  WriteRegDWORD HKCU "${APP_UNINSTALL}" "NoRepair" 1
  CreateDirectory "$SMPROGRAMS\OfflineStore"
  CreateShortcut "$SMPROGRAMS\OfflineStore\OfflineStore.lnk" "$INSTDIR\OfflineStore.exe"
  CreateShortcut "$SMPROGRAMS\OfflineStore\إزالة التثبيت.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\OfflineStore.lnk" "$INSTDIR\OfflineStore.exe"
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$DESKTOP\OfflineStore.lnk"
  Delete "$SMPROGRAMS\OfflineStore\OfflineStore.lnk"
  Delete "$SMPROGRAMS\OfflineStore\إزالة التثبيت.lnk"
  RMDir "$SMPROGRAMS\OfflineStore"
  ; Application binary folder ONLY. Never delete the business database!
  ; Business database remains at %LOCALAPPDATA%\OfflineStore\store.db.
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${APP_UNINSTALL}"
  DeleteRegKey HKCU "${APP_REGKEY}"
SectionEnd
