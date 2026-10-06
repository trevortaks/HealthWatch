# Run as Administrator from the folder containing HealthWatch.exe (output of `dotnet publish`).
$exe = Join-Path $PSScriptRoot "HealthWatch.exe"
sc.exe create HealthWatch binPath= "`"$exe`"" start= auto DisplayName= "HealthWatch Monitor"
sc.exe failure HealthWatch reset= 86400 actions= restart/5000/restart/5000/restart/5000
sc.exe start HealthWatch
