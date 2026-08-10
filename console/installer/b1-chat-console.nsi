; NSIS installer for B1 Chat — Supervision Console
; Compiled with: makensis.exe b1-chat-console.nsi
; Per-user install (no admin rights required).

Unicode true
SetCompressor /SOLID lzma
ManifestSupportedOS all

!define APPNAME "B1 Chat Console"
!define APPDISPLAY "B1 Chat — Supervision Console"
; Matches the csproj's <VersionPrefix>; overridable: makensis /DAPPVERSION=x.y.z
!ifndef APPVERSION
  !define APPVERSION "0.10.4"
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
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

Function .onInit
  ; The published application and espflash are both PE32+ x64 executables. Windows 11 on
  ; ARM64 is accepted because Microsoft supports x64 emulation there.
  ${If} ${IsNativeAMD64}
    ; Native x64: supported.
  ${ElseIf} ${IsNativeARM64}
    ${IfNot} ${AtLeastWin11}
      MessageBox MB_OK|MB_ICONSTOP "${APPDISPLAY} requires Windows 11 on ARM64 computers."
      Abort
    ${EndIf}
  ${Else}
    MessageBox MB_OK|MB_ICONSTOP "${APPDISPLAY} requires a 64-bit x64 computer, or Windows 11 ARM64 with x64 emulation."
    Abort
  ${EndIf}

  ; .NET 8/WPF is bundled, but this build requires Windows 10 version 1607 (build 14393)
  ; or later for the operating-system APIs used by the desktop runtime.
  ${IfNot} ${AtLeastBuild} 14393
    MessageBox MB_OK|MB_ICONSTOP "${APPDISPLAY} requires Windows 10 version 1607 or later. No separate .NET installation is required."
    Abort
  ${EndIf}

  ; Local audio is optional. Windows N/KN editions can omit Media Foundation; all droid,
  ; serial, mesh, firmware and Help features still work, so warn instead of blocking.
  ${DisableX64FSRedirection}
  IfFileExists "$WINDIR\System32\mfplat.dll" media_features_present
  ${EnableX64FSRedirection}
  MessageBox MB_YESNO|MB_ICONEXCLAMATION \
    "Windows Media Foundation was not found.$\r$\n$\r$\nThe console can still control and flash droids, but Sequencer audio playback and waveform previews may not work.$\r$\n$\r$\nOn Windows N/KN, install Media Feature Pack from Settings > Apps > Optional features, then restart Windows.$\r$\n$\r$\nContinue installation?" \
    IDYES media_check_done
  Abort

media_features_present:
  ${EnableX64FSRedirection}
media_check_done:
FunctionEnd

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

  ; Run the actual installed binaries before creating shortcuts or registering the app.
  ; This catches missing/quarantined files and native runtime failures on the destination PC.
  DetailPrint "Checking bundled .NET/WPF runtime and installed payload..."
  nsExec::ExecToStack '"$INSTDIR\${EXENAME}" --verify-install'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "The installed application failed its self-check (exit code $0).$\r$\n$\r$\nAntivirus quarantine or an unsupported Windows component may be responsible. Installation cannot continue safely."
    Abort
  ${EndIf}
  DetailPrint "Application self-check: OK (.NET runtime is bundled)."

  DetailPrint "Checking bundled espflash tool..."
  nsExec::ExecToStack '"$INSTDIR\tools\espflash.exe" --version'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "The bundled espflash tool could not start (exit code $0).$\r$\n$\r$\nFirmware flashing would not work. Installation cannot continue safely."
    Abort
  ${EndIf}
  DetailPrint "espflash self-check: OK ($1)"
  DetailPrint "USB serial drivers are hardware-specific and are checked when a droid is connected."

  WriteUninstaller "$INSTDIR\uninstall.exe"

  !ifndef INSTALLER_SMOKE_TEST
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
  !endif
SectionEnd

Section "Uninstall"
  !ifndef INSTALLER_SMOKE_TEST
    Delete "$SMPROGRAMS\${APPNAME}.lnk"
    Delete "$DESKTOP\${APPNAME}.lnk"
    DeleteRegKey HKCU "${UNINSTKEY}"
  !endif
  RMDir /r "$INSTDIR"
SectionEnd
