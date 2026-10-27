Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "nsDialogs.nsh"
!include "WinMessages.nsh"

; Solid LZMA packs the self-contained app much smaller than the default zlib pass.
SetCompressor /SOLID lzma
SetCompressorDictSize 32

; The window, Start menu, and program folder say Your Local Lib.
Name "Your Local Lib"
Caption "Your Local Lib Setup"
BrandingText "Your Local Lib"
OutFile "..\dist\YourLocalLib-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Your Local Lib"
InstallDirRegKey HKCU "Software\YourLocalLib" "InstallDir"
RequestExecutionLevel user
ShowInstDetails hide
ShowUninstDetails hide

VIProductVersion "1.4.1.0"
VIAddVersionKey /LANG=1033 "ProductName" "Your Local Lib"
VIAddVersionKey /LANG=1033 "FileDescription" "Your Local Lib Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "1.4.1"
VIAddVersionKey /LANG=1033 "ProductVersion" "1.4.1"
VIAddVersionKey /LANG=1033 "CompanyName" "Your Local Lib"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Your Local Lib"

!define MUI_ICON "..\src\YourLocalLib\Assets\app.ico"
!define MUI_UNICON "..\src\YourLocalLib\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_ABORTWARNING_TEXT "Stop installing Your Local Lib?"
!define MUI_UNABORTWARNING_TEXT "Stop removing Your Local Lib?"

!define MUI_BGCOLOR F4EFE6
!define MUI_TEXTCOLOR 1E1A16
!define MUI_DIRECTORYPAGE_BGCOLOR FBF8F3
!define MUI_DIRECTORYPAGE_TEXTCOLOR 1E1A16
!define MUI_INSTFILESPAGE_COLORS "1E1A16 FBF8F3"
!define MUI_INSTFILESPAGE_PROGRESSBAR smooth

!define MUI_WELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP_NOSTRETCH
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP_NOSTRETCH
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "header.bmp"
!define MUI_HEADERIMAGE_BITMAP_NOSTRETCH
!define MUI_HEADERIMAGE_UNBITMAP "header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP_NOSTRETCH

!define MUI_WELCOMEPAGE_TITLE "Your Local Lib"
!define MUI_WELCOMEPAGE_TITLE_3LINES
!define MUI_WELCOMEPAGE_TEXT "A private catalog for the books, movies, music, video games, and board games you already keep in Libib.$\r$\n$\r$\nImport one collection, then scan the barcode on each title. Shelves, search, and audit reports stay on this computer.$\r$\n$\r$\nClick Next to continue."

!define MUI_DIRECTORYPAGE_TEXT_TOP "Install the program for the current Windows user. No administrator password is required.$\r$\n$\r$\nYour catalog is stored in a separate folder and is kept if you uninstall."
!define MUI_DIRECTORYPAGE_TEXT_DESTINATION "Program folder"

!define MUI_INSTFILESPAGE_FINISHHEADER_TEXT "Installation complete"
!define MUI_INSTFILESPAGE_FINISHHEADER_SUBTEXT "Your Local Lib is on this computer."
!define MUI_INSTFILESPAGE_ABORTHEADER_TEXT "Installation stopped"
!define MUI_INSTFILESPAGE_ABORTHEADER_SUBTEXT "Your Local Lib was not fully installed."

!define MUI_FINISHPAGE_TITLE "Ready to scan"
!define MUI_FINISHPAGE_TITLE_3LINES
!define MUI_FINISHPAGE_TEXT "Your Local Lib is installed.$\r$\n$\r$\nExport a collection from the Libib website as CSV, then import it. Or open the program and try the sample library first."
!define MUI_FINISHPAGE_RUN "$INSTDIR\YourLocalLib.exe"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchInstalled
!define MUI_FINISHPAGE_RUN_TEXT "Open Your Local Lib"

!define MUI_UNCONFIRMPAGE_TEXT_TOP "Your Local Lib will be removed from this computer.$\r$\n$\r$\nThe catalog, shelves, and scan history in your local application data folder are left in place."

!define MUI_CUSTOMFUNCTION_GUIINIT ApplyLibraryColors

Var DesktopCheckbox
Var DesktopChoice

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom ShortcutsCreate ShortcutsLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  StrCpy $DesktopChoice ${BST_CHECKED}
FunctionEnd

Function ApplyLibraryColors
  GetDlgItem $0 $HWNDPARENT 1028
  SetCtlColors $0 0x1E1A16 0xF4EFE6
  GetDlgItem $0 $HWNDPARENT 1256
  SetCtlColors $0 0x1E1A16 0xF4EFE6
FunctionEnd

Function ShortcutsCreate
  !insertmacro MUI_HEADER_TEXT "Shortcuts" "A Start menu shortcut is always created."
  nsDialogs::Create 1018
  Pop $0
  SetCtlColors $0 0x1E1A16 0xF4EFE6

  ${NSD_CreateLabel} 0 0 100% 36u "Your Local Lib will be added to the Start menu. You can also keep an icon on the desktop."
  Pop $1
  SetCtlColors $1 0x1E1A16 0xF4EFE6

  ${NSD_CreateCheckbox} 0 46u 100% 14u "Add a shortcut to the desktop"
  Pop $DesktopCheckbox
  SetCtlColors $DesktopCheckbox 0x1E1A16 0xF4EFE6
  ${NSD_SetState} $DesktopCheckbox $DesktopChoice
  nsDialogs::Show
FunctionEnd

Function ShortcutsLeave
  ${NSD_GetState} $DesktopCheckbox $DesktopChoice
FunctionEnd

Function LaunchInstalled
  SetOutPath "$INSTDIR"
  ExecShell "open" "$INSTDIR\YourLocalLib.exe"
FunctionEnd

Section "Install"
  SetOutPath "$INSTDIR"
  File /r \
    /x "*.pdb" \
    /x "createdump.exe" \
    /x "Microsoft.DiaSymReader.Native.amd64.dll" \
    /x "Avalonia.DesignerSupport.dll" \
    /x "Lato-Black.ttf" \
    /x "Lato-BlackItalic.ttf" \
    /x "Lato-SemiBold.ttf" \
    /x "Lato-SemiBoldItalic.ttf" \
    /x "Lato-Medium.ttf" \
    /x "Lato-MediumItalic.ttf" \
    /x "Lato-ExtraBold.ttf" \
    /x "Lato-ExtraBoldItalic.ttf" \
    /x "Lato-Light.ttf" \
    /x "Lato-LightItalic.ttf" \
    /x "Lato-ExtraLight.ttf" \
    /x "Lato-ExtraLightItalic.ttf" \
    /x "Lato-Thin.ttf" \
    /x "Lato-ThinItalic.ttf" \
    "..\publish\win-x64\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\YourLocalLib" "InstallDir" "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\IrishsLibraryTracker"
  DeleteRegKey HKCU "Software\IrishsLibraryTracker"
  ${If} $INSTDIR != "$LOCALAPPDATA\Programs\Irishs Library Tracker"
    RMDir /r "$LOCALAPPDATA\Programs\Irishs Library Tracker"
  ${EndIf}

  Delete "$DESKTOP\Irish's Library Tracker.lnk"
  Delete "$SMPROGRAMS\Irish's Library Tracker\Irish's Library Tracker.lnk"
  Delete "$SMPROGRAMS\Irish's Library Tracker\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Irish's Library Tracker"

  CreateDirectory "$SMPROGRAMS\Your Local Lib"
  CreateShortcut "$SMPROGRAMS\Your Local Lib\Your Local Lib.lnk" "$INSTDIR\YourLocalLib.exe"
  CreateShortcut "$SMPROGRAMS\Your Local Lib\Uninstall.lnk" "$INSTDIR\Uninstall.exe"

  ${If} $DesktopChoice == ${BST_CHECKED}
    CreateShortcut "$DESKTOP\Your Local Lib.lnk" "$INSTDIR\YourLocalLib.exe"
  ${EndIf}

  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib" "DisplayName" "Your Local Lib"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib" "DisplayIcon" "$INSTDIR\YourLocalLib.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib" "DisplayVersion" "1.4.1"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib" "Publisher" "Your Local Lib"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib" "NoRepair" 1
SectionEnd

Section "Uninstall"
  ; The catalog lives in %LOCALAPPDATA%\YourLocalLib and must survive uninstall.
  StrCpy $0 "$LOCALAPPDATA\YourLocalLib"
  StrCpy $1 "$LOCALAPPDATA\IrishsLibraryTracker"
  ${If} $INSTDIR == $0
  ${OrIf} $INSTDIR == $1
    MessageBox MB_OK "The catalog folder was left in place."
  ${Else}
    RMDir /r "$INSTDIR"
  ${EndIf}
  Delete "$DESKTOP\Irish's Library Tracker.lnk"
  Delete "$DESKTOP\Your Local Lib.lnk"
  Delete "$SMPROGRAMS\Irish's Library Tracker\Irish's Library Tracker.lnk"
  Delete "$SMPROGRAMS\Irish's Library Tracker\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Irish's Library Tracker"
  Delete "$SMPROGRAMS\Your Local Lib\Your Local Lib.lnk"
  Delete "$SMPROGRAMS\Your Local Lib\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Your Local Lib"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YourLocalLib"
  DeleteRegKey HKCU "Software\YourLocalLib"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\IrishsLibraryTracker"
  DeleteRegKey HKCU "Software\IrishsLibraryTracker"
SectionEnd
