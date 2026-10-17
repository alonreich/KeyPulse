@echo off
setlocal
cd /d "%~dp0"
python "%~dp0developer_tools\Pull_Code_files.py" %*
