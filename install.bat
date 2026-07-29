@echo off
rem VNyan SoftBody Suite installer launcher
rem Picks your VNyan folder; asks for admin rights ONLY if that folder needs them.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
