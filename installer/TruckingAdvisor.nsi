; TruckingAdvisor Windows installer (NSIS 3).
; Build: makensis /DVERSION=2.3.0 /DPUBLISH_DIR=..\publish\bin installer\TruckingAdvisor.nsi
; Per-user install: no administrator prompt. Requires 64-bit Windows 10 or 11.

; Default: standard 32-bit installer (runs on every 64-bit Windows 10/11).
; /DAMD64 builds a 64-bit installer instead (needs NSIS amd64 stubs; same install steps).
!ifdef AMD64
  Target amd64-unicode
!else
  Unicode true
!endif
!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "LogicLib.nsh"

!define APP "TruckingAdvisor"
!ifndef VERSION
  !define VERSION "2.3.0"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\publish\bin"
!endif
!ifndef OUT_DIR
  !define OUT_DIR "..\publish"
!endif
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"

Name "${APP} ${VERSION}"
OutFile "${OUT_DIR}\TruckingAdvisor-Setup-${VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP}"
InstallDirRegKey HKCU "Software\${APP}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${APP} Setup - ATS telemetry, driving coach and AI CB radio"
VIAddVersionKey "CompanyName" "TruckingAdvisor contributors"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 TruckingAdvisor contributors. MIT License."

!define MUI_ICON "..\Assets\TruckingAdvisor.ico"
!define MUI_UNICON "..\Assets\TruckingAdvisor.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "This installs ${APP} ${VERSION}, a companion app for American Truck Simulator and Euro Truck Simulator 2.$\r$\n$\r$\nIt installs for your Windows account only and doesn't need administrator rights.$\r$\n$\r$\nAfter setup, start the game, then use $\"Install Telemetry Plugin$\" in the app so it can read live data. The app finds the game on any drive, or lets you pick its folder."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP}.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${APP} needs 64-bit Windows 10 or 11."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "${APP} needs Windows 10 or Windows 11."
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Section "${APP} (required)" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  ; The app is one self-contained file; .NET doesn't need to be installed.
  File "${PUBLISH_DIR}\${APP}.exe"
  File "..\Assets\TruckingAdvisor.ico"
  File "..\README.md"
  File "..\LICENSE"
  File "..\THIRD_PARTY_NOTICES.md"
  File "..\SECURITY.md"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APP}"
  CreateShortcut "$SMPROGRAMS\${APP}\${APP}.lnk" "$INSTDIR\${APP}.exe" "" "$INSTDIR\TruckingAdvisor.ico"
  CreateShortcut "$SMPROGRAMS\${APP}\${APP} (demo data).lnk" "$INSTDIR\${APP}.exe" "--demo" "$INSTDIR\TruckingAdvisor.ico"
  CreateShortcut "$SMPROGRAMS\${APP}\Uninstall ${APP}.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\${APP}" "InstallDir" "$INSTDIR"
  ; Shows up in Settings > Apps > Installed apps (current user).
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "TruckingAdvisor contributors"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\TruckingAdvisor.ico"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "${UNINST_KEY}" "URLInfoAbout" "https://github.com/Jcedwar61/TruckingAdvisor"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" 180000
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\${APP}.exe" "" "$INSTDIR\TruckingAdvisor.ico"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "The ${APP} app."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Put a ${APP} shortcut on your desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  Delete "$INSTDIR\${APP}.exe"
  Delete "$INSTDIR\TruckingAdvisor.ico"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.md"
  Delete "$INSTDIR\SECURITY.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  Delete "$DESKTOP\${APP}.lnk"
  Delete "$SMPROGRAMS\${APP}\${APP}.lnk"
  Delete "$SMPROGRAMS\${APP}\${APP} (demo data).lnk"
  Delete "$SMPROGRAMS\${APP}\Uninstall ${APP}.lnk"
  RMDir "$SMPROGRAMS\${APP}"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\${APP}"

  ; Trips, trucker memories, settings and the encrypted API key live here. Keep them unless asked.
  IfSilent keepdata
  MessageBox MB_YESNO|MB_ICONQUESTION "Also delete your saved trips, trucker memories, settings and API key?$\r$\n$\r$\n($LOCALAPPDATA\${APP})" /SD IDNO IDNO keepdata
    RMDir /r "$LOCALAPPDATA\${APP}"
  keepdata:
SectionEnd
