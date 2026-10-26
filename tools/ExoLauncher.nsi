; Exo Launcher — Windows installer (NSIS). Exo's own screens: black page, white
; ink, Inter, the Exo mark; one Install screen, progress, then Launch Exo. Only
; NSIS's bundled nsDialogs and System plugins; /S (the in-app updater) shows none.
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
  !define ICON "..\ExoLauncher\Assets\ExoLauncher.ico"
!endif

; The build records every extracted file, including the web UI and native DLLs.
!system 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "${__FILEDIR__}\installer\New-PayloadManifest.ps1" -PayloadRoot "${PAYLOAD_DIR}" -Version "${PRODUCT_VERSION}"' = 0

!define PRODUCT_NAME "Exo Launcher"
!define PRODUCT_PUBLISHER "Erix (ImAvgErix)"
!define PRODUCT_WEB "https://github.com/ImAvgErix/ExoLauncher"
!ifdef EXO_TEST_ROOT
  ; A walk-through build (/DEXO_TEST_ROOT=<folder>): installs under that folder
  ; with its own registry entries and shortcut names, never over the real Exo.
  !define ROOT "${EXO_TEST_ROOT}"
  !define LINK_NAME "Exo Launcher (test)"
  !define REG_NAME "ExoLauncherTest"
!else
  !define ROOT "$LOCALAPPDATA\ExoLauncher"
  !define LINK_NAME "Exo Launcher"
  !define REG_NAME "ExoLauncher"
!endif
!define PRODUCT_DIR_REGKEY "Software\Microsoft\Windows\CurrentVersion\App Paths\${REG_NAME}.exe"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${REG_NAME}"

Unicode true
ManifestDPIAware true
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
CRCCheck on
XPStyle on

Name "${PRODUCT_NAME}"
Caption "${PRODUCT_NAME} Setup"
UninstallCaption "Uninstall ${PRODUCT_NAME}"
OutFile "${OUTFILE}"
InstallDir "${ROOT}\app"
ShowInstDetails nevershow
ShowUnInstDetails nevershow
; Progress moves on to the finish screen by itself; a failure stays on screen.
AutoCloseWindow true
BrandingText " "
Icon "${ICON}"
UninstallIcon "${ICON}"

!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinMessages.nsh"

; Colors (DESIGN.md): ink, muted, faint, bad, the page, hairlines. COLORREF is 0xBBGGRR.
!define INK "FFFFFF"
!define MUTED "9D9D9D"
!define FAINT "757575"
!define BAD "FF5C5C"
!define PAGE "000000"
!define HAIRLINE "1F1F1F"
!define LINE_SOFT "161616"
!define STATIC_STYLE 0x50000000 ; WS_CHILD | WS_VISIBLE
!define TEXT_STYLE 0x50000080   ; ... | SS_NOPREFIX
!define WM_NEXT 0x408           ; NSIS: WM_NOTIFY_OUTER_NEXT

Var S          ; the Windows scale in percent, snapped to one the pictures exist for
Var Caption    ; the window's own name, drawn in Exo's title band
Var Subtitle
Var FontTitle
Var FontBody
Var FontButton
Var FontSmall
Var FontClose
Var BmpMark
Var BmpButton
Var BmpButtonHover
Var BmpOn
Var BmpOff
Var Band       ; the title band: dragging it moves the window
Var Close      ; the band's ×
Var Button     ; the white capsule
Var ButtonText
Var Link       ; the quiet text action under it
Var Hover      ; "button", "link", "close" or ""
Var Options    ; options shown: 0 or 1
Var OptLine
Var OptLabel
Var OptSwitch
Var OptPath
Var DesktopLink ; desktop shortcut: "" leaves it as it is (silent), "0" or "1"
Var Updating   ; an Exo app is already installed here
Var Status     ; the line under the progress bar
Var Failed     ; an interactive install that did not finish: its reason is in $R6
Var StatusFile ; /STATUSFILE=: where each step is written for Exo's setup window (ExoSetup.exe)
Var NoLaunch   ; /NOLAUNCH: a silent install that leaves Launch Exo to the setup window

Page custom WelcomeShow WelcomeLeave
Page instfiles "" InstShow
Page custom FinishShow FinishLeave
UninstPage custom un.ConfirmShow un.ConfirmLeave
UninstPage instfiles "" un.InstShow

VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright (c) 2026 ${PRODUCT_PUBLISHER}"

; Layout is in 100% pixels on a 460 x 472 window; Px scales one value to this screen.
!define Px "!insertmacro Px"
!macro Px OUT VALUE
  IntOp ${OUT} ${VALUE} * $S
  IntOp ${OUT} ${OUT} / 100
!macroend

!macro Pictures SCALE
  File "/oname=$PLUGINSDIR\mark.bmp" "${__FILEDIR__}\installer\ui\mark-${SCALE}.bmp"
  File "/oname=$PLUGINSDIR\button.bmp" "${__FILEDIR__}\installer\ui\button-${SCALE}.bmp"
  File "/oname=$PLUGINSDIR\button-hover.bmp" "${__FILEDIR__}\installer\ui\button-hover-${SCALE}.bmp"
  File "/oname=$PLUGINSDIR\switch-on.bmp" "${__FILEDIR__}\installer\ui\switch-on-${SCALE}.bmp"
  File "/oname=$PLUGINSDIR\switch-off.bmp" "${__FILEDIR__}\installer\ui\switch-off-${SCALE}.bmp"
!macroend

!macro Font OUT SIZE WEIGHT FACE
  ${Px} $1 ${SIZE}
  IntOp $1 0 - $1
  System::Call 'gdi32::CreateFontW(i r1, i 0, i 0, i 0, i ${WEIGHT}, i 0, i 0, i 0, i 1, i 0, i 0, i 5, i 0, w "${FACE}") p .s'
  Pop ${OUT}
!macroend

!macro Bitmap OUT NAME
  System::Call 'user32::LoadImageW(p 0, w "$PLUGINSDIR\${NAME}.bmp", i 0, i 0, i 0, i 0x10) p .s'
  Pop ${OUT}
!macroend

; A static on the current screen ($0). Clickable ones go through nsDialogs (Ctl).
!macro Static OUT STYLE X Y W H
  ${Px} $1 ${X}
  ${Px} $2 ${Y}
  ${Px} $3 ${W}
  ${Px} $4 ${H}
  System::Call 'user32::CreateWindowExW(i 0, w "STATIC", p 0, i ${STYLE}, i r1, i r2, i r3, i r4, p r0, p 0, p 0, p 0) p .s'
  Pop ${OUT}
!macroend

!macro Ctl OUT STYLE X Y W H TEXT
  ${Px} $1 ${X}
  ${Px} $2 ${Y}
  ${Px} $3 ${W}
  ${Px} $4 ${H}
  nsDialogs::CreateControl STATIC ${STYLE} 0 $1 $2 $3 $4 "${TEXT}"
  Pop ${OUT}
!macroend

!macro SetText HWND TEXT
  SendMessage ${HWND} ${WM_SETTEXT} 0 "STR:${TEXT}"
!macroend

!macro Look HWND FONT FG
  SendMessage ${HWND} ${WM_SETFONT} ${FONT} 1
  SetCtlColors ${HWND} ${FG} ${PAGE}
!macroend

; The same screens build the installer and the uninstaller.
!macro ExoScreens UN

Function ${UN}ExoGuiInit
  System::Call 'user32::GetDpiForWindow(p $HWNDPARENT) i .r0'
  ${If} $0 < 96
    StrCpy $0 96
  ${EndIf}
  IntOp $0 $0 * 100
  IntOp $0 $0 / 96
  InitPluginsDir
  ${If} $0 < 113
    StrCpy $S 100
    !insertmacro Pictures 100
  ${ElseIf} $0 < 138
    StrCpy $S 125
    !insertmacro Pictures 125
  ${ElseIf} $0 < 163
    StrCpy $S 150
    !insertmacro Pictures 150
  ${ElseIf} $0 < 188
    StrCpy $S 175
    !insertmacro Pictures 175
  ${ElseIf} $0 < 225
    StrCpy $S 200
    !insertmacro Pictures 200
  ${ElseIf} $0 < 275
    StrCpy $S 250
    !insertmacro Pictures 250
  ${Else}
    StrCpy $S 300
    !insertmacro Pictures 300
  ${EndIf}
  ; Inter, private to setup: GDI names its instances "Inter Variable Text <weight>".
  File "/oname=$PLUGINSDIR\Inter.ttf" "${__FILEDIR__}\..\ExoLauncher\Assets\Fonts\Inter-Variable.ttf"
  System::Call 'gdi32::AddFontResourceExW(w "$PLUGINSDIR\Inter.ttf", i 0x10, p 0)'
  !insertmacro Font $FontTitle 26 600 "Inter Variable Text SemiBold"
  !insertmacro Font $FontButton 13 600 "Inter Variable Text SemiBold"
  !insertmacro Font $FontBody 13 400 "Inter Variable Text"
  !insertmacro Font $FontSmall 12 400 "Inter Variable Text"
  !insertmacro Font $FontClose 16 400 "Inter Variable Text"
  !insertmacro Bitmap $BmpMark mark
  !insertmacro Bitmap $BmpButton button
  !insertmacro Bitmap $BmpButtonHover button-hover
  !insertmacro Bitmap $BmpOn switch-on
  !insertmacro Bitmap $BmpOff switch-off

  ; No Windows frame: hide every stock control (buttons, branding, header);
  ; Enter and Esc still reach the hidden Next and Cancel.
  StrCpy $1 0
  hide_next:
    System::Call 'user32::FindWindowExW(p $HWNDPARENT, p r1, p 0, p 0) p .r1'
    StrCmp $1 0 hidden
    ShowWindow $1 ${SW_HIDE}
    Goto hide_next
  hidden:

  ; A borderless window (Exo draws its own title band) with Windows 11's
  ; rounded corners and a hairline, centered on the work area.
  System::Call 'user32::GetWindowLongW(p $HWNDPARENT, i -16) i .r1'
  IntOp $1 $1 & 0xFF30FFFF
  IntOp $1 $1 | 0x80020000
  System::Call 'user32::SetWindowLongW(p $HWNDPARENT, i -16, i r1)'
  System::Call 'user32::GetWindowLongW(p $HWNDPARENT, i -20) i .r1'
  IntOp $1 $1 & 0xFFFFFCFE
  System::Call 'user32::SetWindowLongW(p $HWNDPARENT, i -20, i r1)'
  ${Px} $5 460
  ${Px} $6 472
  System::Call '*(i, i, i, i) p .r0'
  System::Call 'user32::SystemParametersInfoW(i 0x30, i 0, p r0, i 0)'
  System::Call '*$0(i .r1, i .r2, i .r3, i .r4)'
  System::Free $0
  IntOp $3 $3 - $1
  IntOp $3 $3 - $5
  IntOp $3 $3 / 2
  IntOp $3 $3 + $1
  IntOp $4 $4 - $2
  IntOp $4 $4 - $6
  IntOp $4 $4 / 2
  IntOp $4 $4 + $2
  System::Call 'user32::SetWindowPos(p $HWNDPARENT, p 0, i r3, i r4, i r5, i r6, i 0x34)'
  System::Call 'dwmapi::DwmSetWindowAttribute(p $HWNDPARENT, i 33, *i 2, i 4)'
  System::Call 'dwmapi::DwmSetWindowAttribute(p $HWNDPARENT, i 34, *i 0x1F1F1F, i 4)'
  SetCtlColors $HWNDPARENT ${INK} ${PAGE}
  ; Every screen fills the window.
  GetDlgItem $0 $HWNDPARENT 1018
  System::Call 'user32::SetWindowPos(p r0, p 0, i 0, i 0, i r5, i r6, i 0x14)'
FunctionEnd

Function ${UN}ExoGuiEnd
  System::Call 'gdi32::RemoveFontResourceExW(w "$PLUGINSDIR\Inter.ttf", i 0x10, p 0)'
FunctionEnd

; NSIS shows its Back and Next again on every page.
Function ${UN}ExoHideFrame
  GetDlgItem $0 $HWNDPARENT 1
  ShowWindow $0 ${SW_HIDE}
  GetDlgItem $0 $HWNDPARENT 2
  ShowWindow $0 ${SW_HIDE}
  GetDlgItem $0 $HWNDPARENT 3
  ShowWindow $0 ${SW_HIDE}
FunctionEnd

; What every screen shares, on screen $0: the title band, the mark, the name
; and a subtitle (text in $5).
Function ${UN}ExoChrome
  SetCtlColors $0 ${INK} ${PAGE}
  !insertmacro Static $Band ${TEXT_STYLE}|${SS_CENTERIMAGE}|${SS_NOTIFY} 14 0 400 32
  !insertmacro Look $Band $FontSmall ${FAINT}
  !insertmacro SetText $Band "$Caption"
  !insertmacro Static $1 ${STATIC_STYLE} 0 31 460 1
  IntOp $2 $S / 100
  ${Px} $3 31
  ${Px} $4 460
  System::Call 'user32::SetWindowPos(p r1, p 0, i 0, i r3, i r4, i r2, i 0x14)'
  SetCtlColors $1 ${LINE_SOFT} ${LINE_SOFT}
  !insertmacro Static $1 ${STATIC_STYLE}|${SS_BITMAP} 194 80 72 72
  SendMessage $1 ${STM_SETIMAGE} ${IMAGE_BITMAP} $BmpMark
  !insertmacro Static $1 ${TEXT_STYLE}|${SS_CENTER} 0 170 460 38
  !insertmacro Look $1 $FontTitle ${INK}
  !insertmacro SetText $1 "${PRODUCT_NAME}"
  !insertmacro Static $Subtitle ${TEXT_STYLE}|${SS_CENTER} 0 210 460 22
  !insertmacro Look $Subtitle $FontBody ${MUTED}
  !insertmacro SetText $Subtitle "$5"
FunctionEnd

; A screen with actions: $5 subtitle, $6 the capsule's label, $7 the link's ("" for none).
Function ${UN}ExoPage
  nsDialogs::Create 1018
  Pop $0
  Call ${UN}ExoChrome
  !insertmacro Ctl $Close ${TEXT_STYLE}|${SS_CENTER}|${SS_CENTERIMAGE}|${SS_NOTIFY} 420 0 40 31 "×"
  !insertmacro Look $Close $FontClose ${MUTED}
  !insertmacro Ctl $Button ${STATIC_STYLE}|${WS_CLIPSIBLINGS}|${SS_BITMAP}|${SS_NOTIFY} 114 268 232 44 ""
  SendMessage $Button ${STM_SETIMAGE} ${IMAGE_BITMAP} $BmpButton
  ; The label sits on the capsule's flat middle, filled the same white.
  !insertmacro Ctl $ButtonText ${TEXT_STYLE}|${SS_CENTER}|${SS_CENTERIMAGE}|${SS_NOTIFY} 136 272 188 36 "$6"
  SendMessage $ButtonText ${WM_SETFONT} $FontButton 1
  SetCtlColors $ButtonText 0A0A0A ${INK}
  System::Call 'user32::SetWindowPos(p $ButtonText, p 0, i 0, i 0, i 0, i 0, i 0x13)'
  StrCpy $Link ""
  ${If} $7 != ""
    !insertmacro Ctl $Link ${TEXT_STYLE}|${SS_CENTER}|${SS_CENTERIMAGE}|${SS_NOTIFY} 160 324 140 24 "$7"
    !insertmacro Look $Link $FontBody ${MUTED}
  ${EndIf}
  StrCpy $Hover ""
FunctionEnd

; Every 30 ms on a screen with actions: hover (the capsule turns white, the
; link and × white; DESIGN.md › Controls) and dragging the title band.
Function ${UN}ExoHover
  System::Call 'user32::GetCursorPos(*l .r1)'
  System::Call 'user32::WindowFromPoint(l r1) p .r1'
  System::Call 'user32::GetAsyncKeyState(i 1) i .r2'
  IntOp $2 $2 & 0x8000
  ${If} $1 == $Band
  ${AndIf} $2 != 0
    System::Call 'user32::ReleaseCapture()'
    SendMessage $HWNDPARENT 0xA1 2 0
    Return
  ${EndIf}
  StrCpy $2 ""
  ${If} $1 == $Button
  ${OrIf} $1 == $ButtonText
    StrCpy $2 button
  ${ElseIf} $1 == $Link
    StrCpy $2 link
  ${ElseIf} $1 == $Close
    StrCpy $2 close
  ${EndIf}
  StrCmp $2 $Hover done
  ${If} $Hover == button
    SendMessage $Button ${STM_SETIMAGE} ${IMAGE_BITMAP} $BmpButton
    SetCtlColors $ButtonText 0A0A0A ${INK}
  ${ElseIf} $Hover == link
    SetCtlColors $Link ${MUTED} ${PAGE}
  ${ElseIf} $Hover == close
    SetCtlColors $Close ${MUTED} ${PAGE}
  ${EndIf}
  ${If} $2 == button
    SendMessage $Button ${STM_SETIMAGE} ${IMAGE_BITMAP} $BmpButtonHover
    SetCtlColors $ButtonText 0A0A0A E8E8E8
  ${ElseIf} $2 == link
    SetCtlColors $Link ${INK} ${PAGE}
  ${ElseIf} $2 == close
    SetCtlColors $Close ${INK} ${PAGE}
  ${EndIf}
  System::Call 'user32::InvalidateRect(p $ButtonText, p 0, i 1)'
  System::Call 'user32::InvalidateRect(p $Link, p 0, i 1)'
  System::Call 'user32::InvalidateRect(p $Close, p 0, i 1)'
  StrCpy $Hover $2
  done:
FunctionEnd

Function ${UN}ExoNext
  Pop $0
  SendMessage $HWNDPARENT ${WM_NEXT} 1 0
FunctionEnd

Function ${UN}ExoCancel
  Pop $0
  SendMessage $HWNDPARENT ${WM_CLOSE} 0 0
FunctionEnd

; The progress screen restyles NSIS's own page: a thin white bar and one status line.
Function ${UN}ExoProgress
  Call ${UN}ExoHideFrame
  FindWindow $0 "#32770" "" $HWNDPARENT
  ; NSIS sizes this page from its own template: fill the window, hide its controls.
  ${Px} $1 460
  ${Px} $2 472
  System::Call 'user32::SetWindowPos(p r0, p 0, i 0, i 0, i r1, i r2, i 0x14)'
  StrCpy $1 0
  hide_next:
    System::Call 'user32::FindWindowExW(p r0, p r1, p 0, p 0) p .r1'
    StrCmp $1 0 hidden
    ShowWindow $1 ${SW_HIDE}
    Goto hide_next
  hidden:
  Call ${UN}ExoChrome
  GetDlgItem $5 $0 1004
  System::Call 'uxtheme::SetWindowTheme(p r5, w "", w "")'
  System::Call 'user32::SetWindowLongW(p r5, i -20, i 0)'
  System::Call 'user32::SetWindowLongW(p r5, i -16, i 0x50000001)'
  ${Px} $1 90
  ${Px} $2 284
  ${Px} $3 280
  ${Px} $4 4
  System::Call 'user32::SetWindowPos(p r5, p 0, i r1, i r2, i r3, i r4, i 0x34)'
  ShowWindow $5 ${SW_SHOW}
  SendMessage $5 0x409 0 0xE4EDF2
  SendMessage $5 0x2001 0 0x2C2C2C
  IntOp $3 $3 + 1
  IntOp $6 $4 + 1
  System::Call 'gdi32::CreateRoundRectRgn(i 0, i 0, i r3, i r6, i r4, i r4) p .r1'
  System::Call 'user32::SetWindowRgn(p r5, p r1, i 1)'
  !insertmacro Static $Status ${TEXT_STYLE}|${SS_CENTER} 50 302 360 20
  !insertmacro Look $Status $FontSmall ${FAINT}
FunctionEnd

!macroend
!insertmacro ExoScreens ""
!insertmacro ExoScreens "un."

!macro Status TEXT
  !insertmacro SetText $Status "${TEXT}"
  ; Exo's setup window runs this silently and shows each step from this file.
  ${If} $StatusFile != ""
    FileOpen $R4 "$StatusFile" a
    FileSeek $R4 0 END
    FileWrite $R4 "${TEXT}$\r$\n"
    FileClose $R4
  ${EndIf}
!macroend

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "${PRODUCT_NAME} requires 64-bit Windows."
    Abort
  ${EndIf}
  SetRegView 64
  ; Exo keeps application binaries in one managed location. User data is held
  ; elsewhere, so setup never needs or accepts an arbitrary destination.
  StrCpy $INSTDIR "${ROOT}\app"
  ; From Exo's setup window: its status file, the desktop shortcut switch, and Launch Exo left to its own button.
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/STATUSFILE=" $StatusFile
  IfErrors 0 +2
    StrCpy $StatusFile ""
  ClearErrors
  ${GetOptions} $0 "/DESKTOP=" $1
  IfErrors +2 0
    StrCpy $DesktopLink $1
  ClearErrors
  ${GetOptions} $0 "/NOLAUNCH" $1
  IfErrors +2 0
    StrCpy $NoLaunch 1
FunctionEnd

Function .onGUIInit
  StrCpy $Caption "${PRODUCT_NAME} Setup"
  Call ExoGuiInit
FunctionEnd

Function .onGUIEnd
  Call ExoGuiEnd
FunctionEnd

Function WelcomeShow
  Call ExoHideFrame
  StrCpy $Updating 0
  IfFileExists "$INSTDIR\ExoLauncher.exe" 0 +2
    StrCpy $Updating 1
  StrCpy $5 "Version ${PRODUCT_VERSION}"
  StrCpy $6 "Install"
  StrCmp $Updating 1 0 +2
    StrCpy $6 "Update"
  StrCpy $7 "Options"
  Call ExoPage
  ${NSD_OnClick} $Close ExoCancel
  ${NSD_OnClick} $Button ExoNext
  ${NSD_OnClick} $ButtonText ExoNext
  ${NSD_OnClick} $Link ToggleOptions

  ; Options: a desktop shortcut, and where Exo lives (fixed, so updates find it).
  !insertmacro Ctl $OptLine ${STATIC_STYLE} 80 364 300 1 ""
  IntOp $4 $S / 100
  ${Px} $1 80
  ${Px} $2 364
  ${Px} $3 300
  System::Call 'user32::SetWindowPos(p $OptLine, p 0, i r1, i r2, i r3, i r4, i 0x14)'
  SetCtlColors $OptLine ${HAIRLINE} ${HAIRLINE}
  !insertmacro Ctl $OptLabel ${TEXT_STYLE}|${SS_CENTERIMAGE}|${SS_NOTIFY} 80 377 220 24 "Desktop shortcut"
  !insertmacro Look $OptLabel $FontBody ${INK}
  !insertmacro Ctl $OptSwitch ${STATIC_STYLE}|${SS_BITMAP}|${SS_NOTIFY} 344 379 36 20 ""
  ${If} $DesktopLink == ""
    StrCpy $DesktopLink 0
    IfFileExists "$DESKTOP\${LINK_NAME}.lnk" 0 +2
      StrCpy $DesktopLink 1
  ${EndIf}
  Call ShowSwitch
  ${NSD_OnClick} $OptLabel ToggleDesktop
  ${NSD_OnClick} $OptSwitch ToggleDesktop
  !insertmacro Ctl $OptPath ${TEXT_STYLE}|${SS_PATHELLIPSIS} 80 414 300 18 "$INSTDIR"
  !insertmacro Look $OptPath $FontSmall ${FAINT}
  StrCpy $Options 1
  Push 0
  Call ToggleOptions

  ${NSD_CreateTimer} ExoHover 30
  nsDialogs::Show
FunctionEnd

Function WelcomeLeave
  ${NSD_KillTimer} ExoHover
FunctionEnd

Function ToggleOptions
  Pop $0
  IntOp $Options $Options ^ 1
  ${If} $Options == 1
    StrCpy $0 ${SW_SHOW}
  ${Else}
    StrCpy $0 ${SW_HIDE}
  ${EndIf}
  ShowWindow $OptLine $0
  ShowWindow $OptLabel $0
  ShowWindow $OptSwitch $0
  ShowWindow $OptPath $0
FunctionEnd

Function ToggleDesktop
  Pop $0
  IntOp $DesktopLink $DesktopLink ^ 1
  Call ShowSwitch
FunctionEnd

Function ShowSwitch
  ${If} $DesktopLink == 1
    SendMessage $OptSwitch ${STM_SETIMAGE} ${IMAGE_BITMAP} $BmpOn
  ${Else}
    SendMessage $OptSwitch ${STM_SETIMAGE} ${IMAGE_BITMAP} $BmpOff
  ${EndIf}
FunctionEnd

Function InstShow
  ${If} $Updating == 1
    StrCpy $5 "Updating to ${PRODUCT_VERSION}"
  ${Else}
    StrCpy $5 "Installing ${PRODUCT_VERSION}"
  ${EndIf}
  Call ExoProgress
FunctionEnd

; The last screen: Launch Exo, or why it did not install (in Bad) and Close.
Function FinishShow
  Call ExoHideFrame
  ${If} $Failed == 1
    StrCpy $5 "Not installed"
    StrCmp $Updating 1 0 +2
      StrCpy $5 "Not updated"
    StrCpy $6 "Close"
    StrCpy $7 ""
  ${Else}
    StrCpy $5 "Installed"
    StrCmp $Updating 1 0 +2
      StrCpy $5 "Updated to ${PRODUCT_VERSION}"
    StrCpy $6 "Launch Exo"
    StrCpy $7 "Close"
  ${EndIf}
  Call ExoPage
  ${NSD_OnClick} $Close ExoNext
  ${If} $Failed == 1
    ${NSD_OnClick} $Button ExoNext
    ${NSD_OnClick} $ButtonText ExoNext
    !insertmacro Ctl $1 ${TEXT_STYLE}|${SS_CENTER}|${SS_EDITCONTROL} 50 328 360 80 "$R6"
    !insertmacro Look $1 $FontSmall ${BAD}
  ${Else}
    ${NSD_OnClick} $Button LaunchExo
    ${NSD_OnClick} $ButtonText LaunchExo
    ${NSD_OnClick} $Link ExoNext
  ${EndIf}
  ${NSD_CreateTimer} ExoHover 30
  nsDialogs::Show
FunctionEnd

Function FinishLeave
  ${NSD_KillTimer} ExoHover
FunctionEnd

Function LaunchExo
  Exec '"$INSTDIR\ExoLauncher.exe"'
  Call ExoNext
FunctionEnd

Function CloseApp
  ; Paths are native process arguments, never interpolated PowerShell source.
  InitPluginsDir
  File /oname=$PLUGINSDIR\Close-ExoLauncher.ps1 "${__FILEDIR__}\installer\Close-ExoLauncher.ps1"
  System::Call 'kernel32::GetCurrentProcessId() i .r0'
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\Close-ExoLauncher.ps1" -AppDirectory "$INSTDIR" -DataDirectory "${ROOT}" -SetupProcessId $0'
  Pop $0
FunctionEnd

Function EnsureWebView2
  ; A Clients pv with no msedgewebview2.exe is a stale registration. The loader
  ; then fails with "Unable to find the specified file". Check the binary.
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ReadRegStr $1 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "location"
  ${If} $0 == ""
    ReadRegStr $0 HKCU "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
    ReadRegStr $1 HKCU "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "location"
  ${EndIf}
  ${If} $0 == ""
    Goto webview_install
  ${EndIf}
  ${If} $1 != ""
    IfFileExists "$1\$0\msedgewebview2.exe" webview_ok webview_try_pf
  ${EndIf}
  webview_try_pf:
  IfFileExists "$PROGRAMFILES32\Microsoft\EdgeWebView\Application\$0\msedgewebview2.exe" webview_ok webview_try_user
  webview_try_user:
  IfFileExists "$LOCALAPPDATA\Microsoft\EdgeWebView\Application\$0\msedgewebview2.exe" webview_ok webview_stale
  webview_stale:
  DetailPrint "WebView2 $0 is registered but its files are missing — repairing..."
  Goto webview_install
  webview_ok:
  DetailPrint "WebView2 Runtime present ($0)"
  Return
  webview_install:
  DetailPrint "WebView2 Runtime not found — downloading bootstrapper..."
  !insertmacro Status "Getting WebView2"
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
  StrCpy $INSTDIR "${ROOT}\app"
  StrCpy $R8 "${ROOT}"
  StrCpy $R3 "0"
  System::Call 'kernel32::GetCurrentProcessId() i .r0'
  StrCpy $R9 "$R8\app.incoming-$0"
  StrCpy $R5 "$R8\app.previous-$0"
  Call EnsureWebView2

  ; Refuse to take ownership of an unrelated non-empty folder. An existing
  ; ExoLauncher.exe marks a managed install; an empty directory is safe.
  IfFileExists "$INSTDIR\ExoLauncher.exe" target_ok
  ; "dir\*.*" matches an empty folder too (its "." entry), and the bootstrap script makes one, so take an empty folder away first: RMDir removes nothing else.
  RMDir "$INSTDIR"
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
  !insertmacro Status "Copying files"
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
  InitPluginsDir
  !insertmacro Status "Checking files"
  File /oname=$PLUGINSDIR\Verify-Payload.ps1 "${__FILEDIR__}\installer\Verify-Payload.ps1"
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\Verify-Payload.ps1" -PayloadRoot "$R9"'
  Pop $0
  ${If} $0 != "0"
    StrCpy $R6 "The extracted installer payload failed verification. No installed files were changed."
    Goto install_fail
  ${EndIf}

  !insertmacro Status "Closing Exo Launcher"
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
    StrCpy $R6 "Exo Launcher's folder is still in use, often by a store launcher Exo opened (Epic Games, Steam, EA app). Quit that launcher from its tray icon, or restart Windows, then update again. Your existing installation was not changed."
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
    ; The last screen says why, in Exo's own window, instead of a message box.
    StrCpy $Failed 1
    SetErrorLevel 1
    Goto install_done
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
  ; Retain the previous payload until the replacement confirms its usable UI.
  ; The helper persists success/failure and restores the prior tree on failure.
  ${If} $R7 == "1"
    !insertmacro Status "Starting the new version"
    File /oname=$PLUGINSDIR\Complete-ExoUpdate.ps1 "${__FILEDIR__}\installer\Complete-ExoUpdate.ps1"
    nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\Complete-ExoUpdate.ps1" -InstallRoot "$R8" -PreviousDirectory "$R5" -Version "${PRODUCT_VERSION}"'
    Pop $0
    ${If} $0 != "0"
      StrCpy $R6 "The replacement did not start successfully. The update result and preserved payload are in $R8."
      Goto install_fail
    ${EndIf}
  ${EndIf}
  Delete "$R8\update-error.log"

  !insertmacro Status "Finishing"
  ; CreateShortCut inherits the current output directory as its working
  ; directory. Point it at the managed app instead of the staging $TEMP path.
  SetOutPath "$INSTDIR"
  CreateDirectory "$SMPROGRAMS"
  CreateShortCut "$SMPROGRAMS\${LINK_NAME}.lnk" "$INSTDIR\ExoLauncher.exe" "" "$INSTDIR\ExoLauncher.exe" 0
  ; Options' switch; a silent update leaves the desktop as it is.
  ${If} $DesktopLink == "1"
    CreateShortCut "$DESKTOP\${LINK_NAME}.lnk" "$INSTDIR\ExoLauncher.exe" "" "$INSTDIR\ExoLauncher.exe" 0
  ${ElseIf} $DesktopLink == "0"
    Delete "$DESKTOP\${LINK_NAME}.lnk"
  ${EndIf}
  ; Setup's current directory would hold the app folder open while the last
  ; screen waits (an update or uninstall from there would find it in use).
  SetOutPath "$TEMP"
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
!ifdef SETUP_SHELL
  ; Exo's setup window stays with the app as its uninstaller, so removing Exo looks like Exo too.
  File "/oname=$INSTDIR\ExoSetup.exe" "${SETUP_SHELL}"
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "UninstallString" '"$INSTDIR\ExoSetup.exe" --uninstall'
  WriteRegStr HKCU "${PRODUCT_UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
!endif

  ; What earlier runs left behind: unfinished staging and backup trees, old receipts, used installers.
  !insertmacro Status "Cleaning up"
  InitPluginsDir
  File /oname=$PLUGINSDIR\Clean-ExoInstall.ps1 "${__FILEDIR__}\installer\Clean-ExoInstall.ps1"
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\Clean-ExoInstall.ps1" -InstallRoot "$R8"'
  Pop $0

  ; The finish screen owns Launch Exo. A silent in-app update has no screens,
  ; so reopen the newly installed build here (Exo's setup window opens it from its own button).
  StrCmp $NoLaunch 1 install_done
  IfSilent silent_launch install_done
  silent_launch:
    Exec '"$INSTDIR\ExoLauncher.exe"'
  install_done:
SectionEnd

Section "Uninstall"
  SetAutoClose true
  !insertmacro Status "Closing Exo Launcher"
  Call un.CloseApp
  ${If} $0 != "0"
    MessageBox MB_OK|MB_ICONSTOP "Exo Launcher is still running. Close it and retry uninstalling." /SD IDOK
    Abort
  ${EndIf}
  ; Exo's power plan goes with Exo: the plan it replaced comes back first.
  File /oname=$PLUGINSDIR\exo-un-power.ps1 "${__FILEDIR__}\installer\Remove-ExoPowerPlan.ps1"
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\exo-un-power.ps1" -DataDirectory "${ROOT}"'
  Pop $0
  !insertmacro Status "Removing files"
  Delete "$SMPROGRAMS\${LINK_NAME}.lnk"
  Delete "$DESKTOP\${LINK_NAME}.lnk"
  ; Refuse to recursively remove anything except the managed app payload.
  IfFileExists "$INSTDIR\ExoLauncher.exe" 0 uninstall_registry
  RMDir /r "$INSTDIR"
  uninstall_registry:
  DeleteRegKey HKCU "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKCU "${PRODUCT_DIR_REGKEY}"
SectionEnd

Function un.onInit
  SetRegView 64
  StrCpy $INSTDIR "${ROOT}\app"
FunctionEnd

Function un.onGUIInit
  StrCpy $Caption "Uninstall ${PRODUCT_NAME}"
  Call un.ExoGuiInit
FunctionEnd

Function un.onGUIEnd
  Call un.ExoGuiEnd
FunctionEnd

Function un.ConfirmShow
  Call un.ExoHideFrame
  StrCpy $5 "Your games and settings stay"
  StrCpy $6 "Uninstall"
  StrCpy $7 "Cancel"
  Call un.ExoPage
  ${NSD_OnClick} $Close un.ExoCancel
  ${NSD_OnClick} $Button un.ExoNext
  ${NSD_OnClick} $ButtonText un.ExoNext
  ${NSD_OnClick} $Link un.ExoCancel
  ${NSD_CreateTimer} un.ExoHover 30
  nsDialogs::Show
FunctionEnd

Function un.ConfirmLeave
  ${NSD_KillTimer} un.ExoHover
FunctionEnd

Function un.InstShow
  StrCpy $5 "Uninstalling"
  Call un.ExoProgress
FunctionEnd

Function un.CloseApp
  InitPluginsDir
  File /oname=$PLUGINSDIR\exo-un-close.ps1 "${__FILEDIR__}\installer\Close-ExoLauncher.ps1"
  System::Call 'kernel32::GetCurrentProcessId() i .r0'
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\exo-un-close.ps1" -AppDirectory "$INSTDIR" -DataDirectory "${ROOT}" -SetupProcessId $0'
  Pop $0
FunctionEnd
