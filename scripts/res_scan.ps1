[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
# Scan all XAML: find StaticResource/DynamicResource references without matching x:Key
$root = 'd:\vs 2026\mcisGame\src\FufuLauncher'
$files = Get-ChildItem -Path $root -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch 'obj|bin' }

# collect all defined keys
$defined = New-Object System.Collections.Generic.HashSet[string]
$defRe = [regex]'x:Key="([A-Za-z0-9_]+)"'
foreach ($f in $files) {
    $c = Get-Content $f.FullName -Raw -Encoding UTF8
    foreach ($m in $defRe.Matches($c)) { [void]$defined.Add($m.Groups[1].Value) }
}

# system/framework keys that are always available
$sysKeys = @('SystemParameters','True','False')
$refRe = [regex]'\{(?:StaticResource|DynamicResource)\s+([A-Za-z0-9_]+)'
$missing = @()
foreach ($f in $files) {
    $c = Get-Content $f.FullName -Raw -Encoding UTF8
    foreach ($m in $refRe.Matches($c)) {
        $k = $m.Groups[1].Value
        if (-not $defined.Contains($k)) {
            $missing += ("{0} -> {1}" -f $f.Name, $k)
        }
    }
}
Write-Host ("Defined keys: " + $defined.Count)
Write-Host ("Missing refs: " + $missing.Count)
$missing | ForEach-Object { Write-Host $_ }

# also scan duplicate x:Key within the same file
$dupRe = [regex]'x:Key="([A-Za-z0-9_]+)"'
foreach ($f in $files) {
    $c = Get-Content $f.FullName -Raw -Encoding UTF8
    $groups = $dupRe.Matches($c) | ForEach-Object { $_.Groups[1].Value } | Group-Object | Where-Object { $_.Count -gt 1 }
    foreach ($g in $groups) { Write-Host ("DUPLICATE: " + $f.Name + " -> " + $g.Name + " x" + $g.Count) }
}
