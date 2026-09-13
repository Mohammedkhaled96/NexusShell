# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| 4.x | ✅ |
| < 4.0 | ❌ |

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report vulnerabilities privately through GitHub:
**Security → Report a vulnerability** on this repository
([direct link](https://github.com/Mohammedkhaled96/NexusShell/security/advisories/new)).

Include the NexusShell version, Windows version, steps to reproduce and the impact.
You can expect an acknowledgement within a few days; fixes are released as a new
version on the [Releases page](https://github.com/Mohammedkhaled96/NexusShell/releases).

## How NexusShell protects your data

- **API keys are encrypted at rest.** Secrets such as the Groq API key are stored in
  `%AppData%\NexusShell\settings.json` as Windows DPAPI ciphertext (`CurrentUser` scope),
  never in plain text and never in this repository.
- **No secrets in source control.** The committed `settings.json` ships with an empty key;
  GitHub secret scanning with push protection and a CI secret scan block leaked credentials.
- **No secrets in logs.** Terminal input and credentials are never written to log files.
- **No telemetry.** The only outbound calls are the AI provider you configure and the
  GitHub Releases update check.
- **Supply chain.** Dependabot tracks NuGet packages and GitHub Actions; CodeQL scans the
  code on every push and pull request; dependency review blocks vulnerable packages in PRs.

## Elevation

`app.manifest` requests administrator rights so NexusShell can register context-menu and
startup entries. Only install builds downloaded from this repository's Releases page.
