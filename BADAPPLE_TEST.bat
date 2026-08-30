@echo off
title BAD APPLE - chatbox OSC test
rem Joue Bad Apple!! dans la chatbox VRChat via OSC (jeu lance + OSC active requis).
rem Usage: double-clic = cadence 200 ms en mode SEND.
rem        BADAPPLE_TEST.bat 200 typing  = mode apercu-de-frappe (pas de filtre anti-spam)
rem        BADAPPLE_TEST.bat 1500        = cadence prudente en mode SEND
rem Ctrl+C arrete et vide la bulle.
cd /d "%~dp0"
python tools\chatbox_probe.py play %1 %2
pause
