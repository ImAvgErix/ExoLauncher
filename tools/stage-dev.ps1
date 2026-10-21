$pub = 'C:\Users\Erix\ExoLauncher\ExoLauncher\bin\x64\Release\net11.0-windows10.0.28000.0\win-x64\publish'
$incoming = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app.incoming-dev'
if (Test-Path $incoming) { Remove-Item $incoming -Recurse -Force }
robocopy $pub $incoming /MIR /NFL /NDL /NJH /NJS | Out-Null
Test-Path (Join-Path $incoming 'ExoLauncher.exe')
