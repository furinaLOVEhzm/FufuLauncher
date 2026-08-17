[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
# Filter login-related lines from app.log tail 200
$mc = 'd:\vs 2026\mcisGame\Start\APP\MCGAME'
$logFile = Get-ChildItem -Path $mc -Directory | ForEach-Object {
    Get-ChildItem -Path $_.FullName -Filter 'app.log' -ErrorAction SilentlyContinue
} | Select-Object -First 1
if ($logFile) {
    $lines = Get-Content $logFile.FullName -Tail 200 -Encoding UTF8
    $hits = $lines | Where-Object { $_ -match '18:2[4-9]|18:3' }
    # output only lines after current session start marker
    $idx = ($lines | Select-String '18:24:08' | Select-Object -Last 1).LineNumber
    if ($idx) { $lines[($idx-1)..($lines.Count-1)] } else { $lines | Select-Object -Last 10 }
}
