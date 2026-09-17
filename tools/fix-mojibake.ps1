# Repairs text that was written as UTF-8 and read back as CP1252 (the Windows default for
# "ANSI"), which is how the damage in this repo happened - not Latin-1. The two differ exactly in
# 0x80-0x9F, which is where the punctuation lives: an em dash is E2 80 94 in UTF-8, and CP1252
# renders 0x80 as EUR and 0x94 as a curly quote. Decoding with Latin-1 leaves those two bytes
# unrecoverable, so the repair has to go back through CP1252.
#
# The repair is that round trip in reverse, applied only to matched runs and only when the bytes
# decode as valid UTF-8. Anything that does not is left exactly as it was.
param([switch]$Apply)

$cp1252 = [System.Text.Encoding]::GetEncoding(1252)
$strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# A lead byte of a UTF-8 sequence as CP1252 would show it, followed by continuation bytes.
$pattern = '[\u00c2-\u00f4][\u0080-\u009f\u00a0-\u00bf\u20ac\u201a\u0192\u201e\u2026\u2020\u2021\u02c6\u2030\u0160\u2039\u0152\u017d\u2018\u2019\u201c\u201d\u2022\u2013\u2014\u02dc\u2122\u0161\u203a\u0153\u017e\u0178]+'
$changed = 0

Get-ChildItem -Path src, tests -Recurse -Include *.cs, *.xaml -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object {
    $path = $_.FullName
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }
    if ($text -notmatch $pattern) { return }

    $repaired = [regex]::Replace($text, $pattern, {
        param($m)
        $raw = $m.Value
        try {
            $buf = $cp1252.GetBytes($raw)
            # Round-trips back to the same characters only if every one had a CP1252 byte.
            if ($cp1252.GetString($buf) -ne $raw) { return $raw }
            return $strictUtf8.GetString($buf)
        }
        catch {
            return $raw
        }
    })

    if ($repaired -eq $text) { return }

    $rel = $path.Replace((Get-Location).Path + '\', '')
    Write-Host ("{0}  ({1} run(s))" -f $rel, [regex]::Matches($text, $pattern).Count)

    if ($Apply) {
        $enc = if ($hasBom) { New-Object System.Text.UTF8Encoding($true) } else { $utf8NoBom }
        [System.IO.File]::WriteAllText($path, $repaired, $enc)
    }
    $script:changed++
}

Write-Host ""
Write-Host "Files affected: $changed"
if (-not $Apply) { Write-Host "Dry run. Re-run with -Apply to write." }
