---
name: powershell-scripting
description: Conventions and safety checklist for writing robust PowerShell scripts and one-liners on Windows (error handling, quoting, paths, encoding, elevation).
---
# PowerShell scripting

Use this checklist when writing or reviewing PowerShell for the user.

## Robust scripts
1. Start scripts with `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'` so failures are not silently ignored.
2. Wrap risky blocks in `try { ... } catch { Write-Error $_; exit 1 }`.
3. Use full cmdlet and parameter names in scripts (`Get-ChildItem -Path`), aliases only in interactive one-liners.
4. Build paths with `Join-Path`, never by string concatenation; quote paths that may contain spaces.
5. Use `-LiteralPath` when a path may contain `[` or `]`.
6. For files read by other tools, write UTF-8 explicitly: `Set-Content -Encoding utf8` (Windows PowerShell 5.1 defaults to the ANSI code page).

## Quoting
- Single quotes are literal; double quotes expand `$variables` and `$(subexpressions)`.
- Escape character is the backtick, not backslash.
- To pass arguments to native executables that contain special characters, prefer an array: `& git @('log', '--format=%H')`.

## Safety
- Preview destructive operations with `-WhatIf` first (`Remove-Item -Recurse -WhatIf`).
- Say when an elevated (Run as administrator) session is required; check with
  `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')`.
- Do not change the execution policy machine-wide; use `-Scope Process` when a script must run.

## Windows PowerShell 5.1 vs PowerShell 7
- `&&` and `||` pipeline chain operators, the ternary operator and `??` exist only in PowerShell 7+.
- `ConvertFrom-Json -AsHashtable` exists only in PowerShell 7+.
- Detect the version with `$PSVersionTable.PSVersion`.
