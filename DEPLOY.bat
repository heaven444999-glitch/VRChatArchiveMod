@echo off
title VRCHAT ARCHIVE MOD - deploiement
setlocal
rem Copie le DLL fraichement construit vers :
rem   1) la bibliotheque de mods du site  (F:\VRCHAT ARCHIVE\DATA\MODS)
rem   2) le pack que le jeu charge reellement (BepInEx\plugins)
rem VRChat doit etre FERME : le plugin est verrouille tant que le jeu tourne.

set "SRC=F:\PROJETS\VRChatArchiveMod\bin\Release\VRChatArchiveMod.dll"
set "MODS=F:\VRCHAT ARCHIVE\DATA\MODS\VRChatArchiveMod.dll"
set "PACK=G:\GAMES\MODES\VRCHAT\VRChatArchiveClient\data\patch\BepInEx\plugins\VRChatArchiveMod.dll"

if not exist "%SRC%" (
  echo [ERREUR] build introuvable : %SRC%
  pause & exit /b 1
)

tasklist /FI "IMAGENAME eq VRChat.exe" 2>nul | find /I "VRChat.exe" >nul
if not errorlevel 1 (
  echo [ATTENTION] VRChat est ouvert - ferme le jeu puis relance ce script.
  pause & exit /b 1
)

copy /Y "%SRC%" "%MODS%" >nul && echo [OK] bibliotheque MODS mise a jour
copy /Y "%SRC%" "%PACK%" >nul && echo [OK] pack du jeu mis a jour  ^(c'est celui-ci que VRChat charge^)

echo.
echo Termine - relance VRChat.
pause

