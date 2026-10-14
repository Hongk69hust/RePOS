Set WshShell = CreateObject("WScript.Shell")
WshShell.CurrentDirectory = "C:\Users\P1ngMe0n\PCMonitorSender"
WshShell.Run "cmd.exe /c dotnet run --project ""C:\Users\P1ngMe0n\PCMonitorSender\PCMonitorSender.csproj"" --configuration Release", 0, False
Set WshShell = Nothing