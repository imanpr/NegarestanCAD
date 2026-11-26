@echo off
chcp 65001 >nul
echo Downloading official Vazirmatn TTF fonts into src\NegarestanCAD\Fonts\ ...
mkdir src\NegarestanCAD\Fonts 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$base = 'https://raw.githubusercontent.com/rastikerdar/vazirmatn/master/fonts/ttf';" ^
  "$files = @('Vazirmatn-Regular.ttf','Vazirmatn-Medium.ttf','Vazirmatn-SemiBold.ttf','Vazirmatn-Bold.ttf');" ^
  "foreach ($f in $files) { try { Invoke-WebRequest -Uri \"$base/$f\" -OutFile \"src\NegarestanCAD\Fonts\$f\" -UseBasicParsing; Write-Host \"Downloaded $f\" } catch { Write-Host \"Warning: Could not download $f\" } }"

echo Done!
