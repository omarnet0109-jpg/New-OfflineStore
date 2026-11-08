!include "FileFunc.nsh"
; Both interactive and silent installs require a dedicated application folder.
Function ValidateInstallDirectory
  ; NSIS GetFullPathName can empty the result for a nonexistent path.
  ; Win32 canonicalization accepts a new folder without creating it.
  StrCpy $5 "invalid canonical path"
  System::Call 'kernel32::GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r6, p 0) i .r7'
  IntCmp $7 0 invalid invalid 0
  IntCmp $7 ${NSIS_MAX_STRLEN} invalid 0 invalid
  StrCpy $INSTDIR $6
  ${GetRoot} "$INSTDIR" $0
  StrCpy $5 "drive root"
  StrCmp "$INSTDIR" "$0" invalid
  StrCmp "$INSTDIR" "$0\" invalid
  StrCpy $5 "protected folder"
  StrCmp "$INSTDIR" "$WINDIR" invalid
  StrCmp "$INSTDIR" "$PROGRAMFILES" invalid
  StrCmp "$INSTDIR" "$PROFILE" invalid
  StrCmp "$INSTDIR" "$DESKTOP" invalid
  StrCmp "$INSTDIR" "$LOCALAPPDATA\OfflineStore" invalid
  StrCpy $1 "$INSTDIR" 2
  StrCmp $1 "\\" invalid
  StrCpy $5 "database file in destination"
  IfFileExists "$INSTDIR\*.db" invalid
  IfFileExists "$INSTDIR\OfflineStore.exe" 0 checkEmpty
  IfFileExists "$INSTDIR\Uninstall.exe" done checkEmpty
  checkEmpty:
    StrCpy $5 "nonempty destination"
    ClearErrors
    StrCpy $3 ""
    FindFirst $2 $3 "$INSTDIR\*.*"
    IfErrors done
    loop:
      StrCmp $3 "" empty
      StrCmp $3 "." next
      StrCmp $3 ".." next
      FindClose $2
      Goto invalid
    next:
      FindNext $2 $3
      Goto loop
    empty:
      FindClose $2
  done:
    ClearErrors
    Return
  invalid:
!ifdef INSTALLER_QA
    FileOpen $4 "$TEMP\OfflineStore-Path-Validation.txt" a
    FileWrite $4 "Rejected: $5 | dir=$INSTDIR | root=$0 | file=$3$\r$\n"
    FileClose $4
!endif
    Abort
FunctionEnd
Function .onVerifyInstDir
  Call ValidateInstallDirectory
FunctionEnd
