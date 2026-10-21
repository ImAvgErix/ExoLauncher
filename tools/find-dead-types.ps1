# Lists C# types under ExoLauncher/ that no other source file references.
# A hit here is a candidate, not a verdict: JSON DTOs, XAML-backed types and
# analyzer/reflection targets can be live with zero textual references.
param([string]$Root = (Join-Path $PSScriptRoot '..'))

$src = Get-ChildItem (Join-Path $Root 'ExoLauncher') -Recurse -File -Include *.cs, *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$text = @{}
foreach ($f in $src) { $text[$f.FullName] = Get-Content $f.FullName -Raw }

$decl = [regex]'(?m)^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|readonly\s+|partial\s+|unsafe\s+)*(?:class|struct|record(?:\s+struct|\s+class)?|interface|enum)\s+([A-Z]\w*)'
$results = foreach ($f in $src | Where-Object Extension -eq '.cs') {
    $body = $text[$f.FullName]
    foreach ($m in $decl.Matches($body)) {
        $name = $m.Groups[1].Value
        $pattern = "\b$([regex]::Escape($name))\b"
        # Own-file mentions beyond the declaration itself count as use too
        # (private nested DTOs, file-local helpers).
        $refs = ([regex]::Matches($body, $pattern)).Count - 1
        foreach ($k in $text.Keys) {
            if ($k -eq $f.FullName) { continue }
            if ($text[$k] -match $pattern) { $refs++ }
        }
        if ($refs -le 0) {
            [pscustomobject]@{ Type = $name; File = $f.FullName.Substring($Root.Length + 1); KB = [math]::Round($f.Length / 1KB, 1) }
        }
    }
}
$results | Sort-Object File, Type | Format-Table -AutoSize
