taskkill /IM DBPBusinessCardEditable.exe /F 2>$null
taskkill /IM DBPQRPermanent.exe /F 2>$null
Start-Sleep -Seconds 2
Remove-Item -Recurse -Force "c:\Users\cdepurificacion\source\repos\QR_DBP\QR_DBP"
Remove-Item -Force "c:\Users\cdepurificacion\source\repos\QR_DBP\QR_DBP.sln"
Remove-Item -Force "c:\Users\cdepurificacion\source\repos\QR_DBP\DBPBusinessCardEditable\QR_DBP.csproj"
Write-Host "Cleanup complete."
