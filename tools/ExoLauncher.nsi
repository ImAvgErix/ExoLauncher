; Exo Launcher — standard Windows installer (NSIS)
!ifndef PRODUCT_VERSION
  !searchparse /file "..\VERSION" "" PRODUCT_VERSION
!endif
!ifndef PAYLOAD_DIR
  !error "PAYLOAD_DIR required"
!endif
!ifndef OUTFILE
  !define OUTFILE "ExoLauncher-Setup.exe"
!endif
!ifndef ICON
  !define ICON "ExoLauncher.ico"
!endif

!define PRODUCT_NAME "Exo Launcher"
!define PRODUCT_PUBLISHER "Erix (ImAvgErix)"
!define PRODUCT_WEB "https://github.com/ImAvgErix/ExoLauncher"
!define PRODUCT_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\ExoLauncher.exe"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ExoLauncher"

Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
CRCCheck on

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\ExoLauncher\app"
ShowInstDetails show
ShowUnInstDetails show
BrandingText "${PRODUCT_NAME} ${PRODUCT_VERSION}"
Icon "${ICON}"
UninstallIcon "${ICON}"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_WELCOMEPAGE_TITLE "Install ${PRODUCT_NAME}"
!define MUI_WELCOMEPAGE_TEXT "Setup will install ${PRODUCT_NAME} on your computer.$\r$\n$\r$\nOne library UI. Store clients as invisible backends.$\r$\n$\r$\nClick Next to continue."
!define MUI_FINISHPAGE_RUN "$INSTDIR\ExoLauncher.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${PRODUCT_NAME}"
!define MUI_FINISHPAGE_LINK "${PRODUCT_NAME} on GitHub"
!define MUI_FINISHPAGE_LINK_LOCATION "${PRODUCT_WEB}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (c) 2026 ${PRODUCT_PUBLISHER}"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "${PRODUCT_NAME} requires 64-bit Windows."
    Abort
  ${EndIf}
  SetRegView 64
  ; Exo keeps application binaries in one managed location. User data is held
  ; elsewhere, so setup never needs or accepts an arbitrary destination.
  StrCpy $INSTDIR "$LOCALAPPDATA\ExoLauncher\app"
FunctionEnd

Function CloseApp
  ; Stop only the installed launcher. The setup executable may also be named
  ; ExoLauncher.exe in older releases and must never terminate itself. Write a
  ; short script instead of nesting PowerShell inside -Command: cmd.exe strips
  ; quotes from that form in some locales, making the exact-path filter a no-op.
  System::Call 'kernel32::GetCurrentProcessId() i .r0'
  StrCpy $R2 "$TEMP\exo-launcher-close-$0.ps1"
  FileOpen $1 "$R2" w
  FileWrite $1 "$$ErrorActionPreference = 'SilentlyContinue'$\r$\n"
  FileWrite $1 "$$target = [IO.Path]::GetFullPath('$INSTDIR\ExoLauncher.exe')$\r$\n"
  FileWrite $1 "$$webViewRoot = [IO.Path]::GetFullPath('$INSTDIR\ExoLauncher.exe.WebView2')$\r$\n"
  FileWrite $1 "function Stop-ExoWebView {$\r$\n"
  FileWrite $1 "  $$webDeadline = [DateTime]::UtcNow.AddSeconds(5)$\r$\n"
  FileWrite $1 "  do {$\r$\n"
  FileWrite $1 "    $$web = @(Get-CimInstance Win32_Process | Where-Object { $$_.Name -eq 'msedgewebview2.exe' -and $$_.CommandLine -and $$_.CommandLine.Contains($$webViewRoot, [StringComparison]::OrdinalIgnoreCase) })$\r$\n"
  FileWrite $1 "    foreach ($$process in $$web) { Stop-Process -Id $$process.ProcessId -Force }$\r$\n"
  FileWrite $1 "    if ($$web.Count -eq 0) { return }$\r$\n"
  FileWrite $1 "    Start-Sleep -Milliseconds 100$\r$\n"
  FileWrite $1 "  } while ([DateTime]::UtcNow -lt $$webDeadline)$\r$\n"
  FileWrite $1 "}$\r$\n"
  ; An in-app update starts setup before the WebView bridge can return and run
  ; Exo's normal Closed cleanup. Give that exact installed process time to
  ; flush sessions/settings and exit itself before using the bounded fallback.
  FileWrite $1 "if ($$env:EXO_SILENT_INSTALL -eq '1') {$\r$\n"
  FileWrite $1 "  $$graceDeadline = [DateTime]::UtcNow.AddSeconds(8)$\r$\n"
  FileWrite $1 "  do {$\r$\n"
  FileWrite $1 "    $$matches = @(Get-CimInstance Win32_Process | Where-Object { $$_.Name -eq 'ExoLauncher.exe' -and $$_.ExecutablePath -and [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($$_.ExecutablePath), $$target) })$\r$\n"
  FileWrite $1 "    if ($$matches.Count -eq 0) { Stop-ExoWebView; exit 0 }$\r$\n"
  FileWrite $1 "    Start-Sleep -Milliseconds 200$\r$\n"
  FileWrite $1 "  } while ([DateTime]::UtcNow -lt $$graceDeadline)$\r$\n"
  FileWrite $1 "}$\r$\n"
  FileWrite $1 "$$deadline = [DateTime]::UtcNow.AddSeconds(5)$\r$\n"
  FileWrite $1 "do {$\r$\n"
  FileWrite $1 "  $$matches = @(Get-CimInstance Win32_Process | Where-Object { $$_.Name -eq 'ExoLauncher.exe' -and $$_.ExecutablePath -and [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($$_.ExecutablePath), $$target) })$\r$\n"
  FileWrite $1 "  foreach ($$process in $$matches) { Stop-Process -Id $$process.ProcessId -Force }$\r$\n"
  FileWrite $1 "  if ($$matches.Count -eq 0) { Stop-ExoWebView; exit 0 }$\r$\n"
  FileWrite $1 "  Start-Sleep -Milliseconds 200$\r$\n"
  FileWrite $1 "} while ([DateTime]::UtcNow -lt $$deadline)$\r$\n"
  FileWrite $1 "$$remaining = @(Get-CimInstance Win32_Process | Where-Object { $$_.Name -eq 'ExoLauncher.exe' -and $$_.ExecutablePath -and [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($$_.ExecutablePath), $$target) })$\r$\n"
  FileWrite $1 "if ($$remaining.Count -gt 0) { exit 1 }$\r$\n"
  FileWrite $1 "Stop-ExoWebView$\r$\n"
  FileClose $1
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$R2"'
  Pop $0
  Delete "$R2"
  Sleep 300
FunctionEnd

Function EnsureWebView2
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == ""
    ReadRegStr $0 HKCU "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
  ${If} $0 != ""
    DetailPrint "WebView2 Runtime present ($0)"
    Return
  ${EndIf}
  DetailPrint "WebView2 Runtime not found — downloading bootstrapper..."
  FileOpen $3 "$TEMP\exo-launcher-webview2.ps1" w
  FileWrite $3 "$$ErrorActionPreference = 'Stop'$\r$\n"
  FileWrite $3 "$$uri = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'$\r$\n"
  FileWrite $3 "$$out = Join-Path $$env:TEMP 'MicrosoftEdgeWebview2Setup.exe'$\r$\n"
  FileWrite $3 "Invoke-WebRequest -UseBasicParsing -Uri $$uri -OutFile $$out$\r$\n"
  FileWrite $3 "$$signature = Get-AuthenticodeSignature -LiteralPath $$out$\r$\n"
  FileWrite $3 "if ($$signature.Status -ne 'Valid' -or $$signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$$)') { Remove-Item -LiteralPath $$out -Force -ErrorAction SilentlyContinue; throw 'WebView2 bootstrapper signature is not valid for Microsoft Corporation.' }$\r$\n"
  FileClose $3
  nsExec::ExecToLog 'powershell -NoProfile -ExecutionPolicy Bypass -File "$TEMP\exo-launcher-webview2.ps1"'
  Pop $1
  Delete "$TEMP\exo-launcher-webview2.ps1"
  ${If} $1 != "0"
    DetailPrint "WebView2 download failed ($1). Continuing."
    Return
  ${EndIf}
  ExecWait '"$TEMP\MicrosoftEdgeWebview2Setup.exe" /silent /install' $2
  Delete "$TEMP\MicrosoftEdgeWebview2Setup.exe"
FunctionEnd

Section "Exo Launcher" SecApp
  SectionIn RO
  StrCpy $INSTDIR "$LOCALAPPDATA\ExoLauncher\app"
  StrCpy $R8 "$LOCALAPPDATA\ExoLauncher"
  StrCpy $R3 "0"
  System::Call 'kernel32::GetCurrentProcessId() i .r0'
  StrCpy $R9 "$R8\app.incoming-$0"
  StrCpy $R5 "$R8\app.previous-$0"
  Call EnsureWebView2

  ; Refuse to take ownership of an unrelated non-empty folder. An existing
  ; ExoLauncher.exe marks a managed install; an empty directory is safe.
  IfFileExists "$INSTDIR\ExoLauncher.exe" target_ok
  IfFileExists "$INSTDIR\*.*" unmanaged_target target_empty
  unmanaged_target:
    StrCpy $R6 "The Exo Launcher app folder contains files from another application. Setup left that folder unchanged."
    Goto install_fail
  target_empty:
    RMDir "$INSTDIR"
  target_ok:

  ; Stage to a per-setup folder, then swap — avoids both half-overwriting
  ; locked trees and deleting a pre-existing directory owned by something else.
  IfFileExists "$R9\*.*" staging_collision
  IfFileExists "$R5\*.*" backup_collision
  ; An empty leftover is harmless to remove. Never recursively delete a
  ; pre-existing backup candidate that setup did not create.
  RMDir "$R9"
  RMDir "$R5"
  CreateDirectory "$R9"
  StrCpy $R3 "1"
  SetOutPath "$R9"
  File /r "${PAYLOAD_DIR}\*.*"
  Goto staging_ready
  staging_collision:
    StrCpy $R6 "Setup could not create a private staging folder. No installed files were changed."
    Goto install_fail
  backup_collision:
    StrCpy $R6 "Setup found an unexpected rollback folder. No installed files were changed."
    Goto install_fail
  staging_ready:

  IfFileExists "$R9\ExoLauncher.exe" 0 missing_payload
  Goto payload_ok
  missing_payload:
    StrCpy $R6 "Install package is incomplete (ExoLauncher.exe missing)."
    Goto install_fail
  payload_ok:
  ; SetOutPath changes setup's current directory. Leave the staged tree before
  ; attempting to rename or remove it, otherwise Windows keeps its root locked.
  SetOutPath "$TEMP"

  Call CloseApp
  ${If} $0 != "0"
    StrCpy $R6 "Exo Launcher is still running and could not be stopped safely. Your existing installation was not changed."
    Goto install_fail
  ${EndIf}
  ; Move the complete live tree aside, then atomically promote the complete
  ; incoming tree. Never copy a payload over the live app in place.
  StrCpy $R7 "0"
  IfFileExists "$INSTDIR\ExoLauncher.exe" 0 no_old
    ClearErrors
    Rename "$INSTDIR" "$R5"
    IfErrors swap_prepare_fail
    StrCpy $R7 "1"
  no_old:
  CreateDirectory "$R8"
  ClearErrors
  Rename "$R9" "$INSTDIR"
  IfErrors swap_commit_fail
  StrCpy $R3 "0"
  IfFileExists "$INSTDIR\ExoLauncher.exe" 0 swap_verify_fail
  Goto swap_ok

  swap_prepare_fail:
    StrCpy $R6 "Exo Launcher is still running or its install folder is locked. Your existing installation was not changed."
    Goto install_fail

  swap_commit_fail:
    ${If} $R7 == "1"
      ; Rename is same-volume and atomic. Remove only an empty failed target;
      ; never recursively delete a path that was not promoted by setup.
      RMDir "$INSTDIR"
      ClearErrors
      Rename "$R5" "$INSTDIR"
      IfErrors rollback_fail
    ${EndIf}
    StrCpy $R6 "The new app could not be installed. Your previous installation was restored."
    Goto install_fail

  swap_verify_fail:
    RMDir /r "$INSTDIR"
    ${If} $R7 == "1"
      ClearErrors
      Rename "$R5" "$INSTDIR"
      IfErrors rollback_fail
    ${EndIf}
    StrCpy $R6 "The installed app failed verification. Your previous installation was restored."
    Goto install_fail

  rollback_fail:
    StrCpy $R6 "Setup could not restore the previous installation automatically. It remains preserved in $R5."
    Goto install_fail

  install_fail:
    ${If} $R3 == "1"
      RMDir /r "$R9"
    ${EndIf}
    IfSilent silent_install_fail install_message
  install_message:
    MessageBox MB_OK|MB_ICONSTOP "$R6"
    Goto install_abort
  silent_install_fail:
    CreateDirectory "$R8"
    FileOpen $R4 "$R8\update-error.log" w
    FileWrite $R4 "$R6$\r$\n"
    FileClose $R4
    ; If setup already stopped the current build and rollback succeeded, put
    ; that build back on screen. Single-instance redirection makes this safe
    ; even when it was never stopped.
    IfFileExists "$INSTDIR\ExoLauncher.exe" 0 install_abort
    Exec '"$INSTDIR\ExoLauncher.exe"'
  install_abort:
    SetErrorLevel 1
    Abort

  swap_ok:
  ; $R5 is known to be the exact previous managed app tree moved above.
  RMDir /r "$R5"
  Delete "$R8\update-error.log"

  ; CreateShortCut inherits the current output directory as its working
  ; directory. Point it at the managed app instead of the staging $TEMP path.
  SetOutPath "$INSTDIR"
  CreateDirectory "$SMPROGRAMS"
  CreateShortCut "$SMPROGRAMS\Exo Launcher.lnk" "$INSTDIR\ExoLauncher.exe" "" "$INSTDIR\ExoLauncher.exe" 0
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${PRODUCT_DIR_REGKEY}" "" "$INSTDIR\ExoLauncher.exe"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\ExoLauncher.exe"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "URLInfoAbout" "${PRODUCT_WEB}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${PRODUCT_UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${PRODUCT_UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${PRODUCT_UNINST_KEY}" "EstimatedSize" "$0"

  ; The interactive finish page owns its launch checkbox. A silent in-app
  ; update has no finish page, so reopen the newly installed build here.
  IfSilent silent_launch install_done
  silent_launch:
    Exec '"$INSTDIR\ExoLauncher.exe"'
  install_done:
SectionEnd

Section "Uninstall"
  Call un.CloseApp
  Delete "$SMPROGRAMS\Exo Launcher.lnk"
  ; Refuse to recursively remove anything except the managed app payload.
  IfFileExists "$INSTDIR\ExoLauncher.exe" 0 uninstall_registry
  RMDir /r "$INSTDIR"
  uninstall_registry:
  DeleteRegKey HKCU "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKCU "${PRODUCT_DIR_REGKEY}"
SectionEnd

Function un.onInit
  SetRegView 64
  StrCpy $INSTDIR "$LOCALAPPDATA\ExoLauncher\app"
FunctionEnd

Function un.CloseApp
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "$$target = [IO.Path]::GetFullPath(''$INSTDIR\ExoLauncher.exe''); Get-CimInstance Win32_Process -Filter ''Name = $\"ExoLauncher.exe$\"'' -ErrorAction SilentlyContinue | Where-Object { try { [IO.Path]::GetFullPath($$_.ExecutablePath) -eq $$target } catch { $$false } } | ForEach-Object { Invoke-CimMethod -InputObject $$_ -MethodName Terminate -ErrorAction SilentlyContinue | Out-Null }"'
  Pop $0
  Sleep 400
FunctionEnd
