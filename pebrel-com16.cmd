@echo off
setlocal
set "PEBREL_CONFIG_DIR=D:\Apps\Pebrel-Serial\data"
start "" "D:\Apps\Pebrel-Serial\pebrel.exe" --data-dir "D:\Apps\Pebrel-Serial\data" --config-file "D:\Apps\Pebrel-Serial\pebrel.lua" --working-directory "%~dp0." --shell "profile:powershell|com2tcp-com16-utf8"
