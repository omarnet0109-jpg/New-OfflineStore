Unicode true
Name "OfflineStore path validation test"
OutFile "${OUTPUT_EXE}"
RequestExecutionLevel user
InstallDir "$TEMP\OfflineStorePathProbe"
SetCompressor /SOLID lzma
!include "..\..\installer\InstallPathSafety.nsh"
Section
  Call ValidateInstallDirectory
  SetErrorLevel 0
SectionEnd
