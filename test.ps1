 = New-Object System.Diagnostics.ProcessStartInfo; .FileName = 'notepad.exe C:\test.txt'; .UseShellExecute = True; [System.Diagnostics.Process]::Start()
