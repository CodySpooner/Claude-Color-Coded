@echo off
rem Compiles ClaudeColorCoded.exe with the C# compiler built into Windows. Nothing to install.
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
"%FW%\csc.exe" /nologo /unsafe /target:winexe /platform:x64 /optimize /out:"%~dp0ClaudeColorCoded.exe" /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" "%~dp0claude-color-coded.cs"
