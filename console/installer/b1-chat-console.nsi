; NSIS installer for B1 Chat — Supervision Console
; Compiled with: makensis.exe b1-chat-console.nsi
; Per-user install (no admin rights required).

Unicode true
SetCompressor /SOLID lzma

!define APPNAME "B1 Chat Console"
!define APPDISPLAY "B1 Chat — Supervision Console"
; Matches the csproj's <VersionPrefix>; overridable: makensis /DAPPVERSION=x.y.z
!ifndef APPVERSION
  !define APPVERSION "0.8.0"
!endif
!define PUBLISHER "stefe"
!define EXENAME "b1-chat-console.exe"
!define PUBLISHDIR "..\bin\Release\net8.0-windows\win-x64\publish"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\B1ChatConsole"

Name "${APPDISPLAY}"
OutFile "b1-chat-console-setup-${APPVERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\B1ChatConsole"
InstallDirRegKey HKCU "${UNINSTKEY}" "InstallLocation"
RequestExecutionLevel user

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ICON "..\Assets\AppIcon.ico"
!define MUI_UNICON "..\Assets\AppIcon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APPDISPLAY}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Application" SecApp
  SectionIn RO

  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "${PUBLISHDIR}\*.*"

  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Shortcuts (Start menu + desktop)
  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"

  ; "Installed apps" entry (per-user)
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPDISPLAY}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${APPVERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXENAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTKEY}"
SectionEnd
