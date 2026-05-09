# Security Policy

## Supported versions

This project is pre-release. Until V1.0 ships, only the `main` branch is
supported. Once V1 lands, the most recent two minor versions will receive
security fixes.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Instead, report privately by either:

1. Using GitHub's [private vulnerability reporting][gh-pvr] for this repo
   (Security tab → "Report a vulnerability"), **or**
2. Opening a GitHub issue with the title `[SECURITY] please contact me` and
   no other details — a maintainer will reach out via your account email
   to set up a private channel.

[gh-pvr]: https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability

Please include:

- A description of the issue
- Steps to reproduce
- Affected version / commit
- Potential impact
- Any suggested mitigation

We will acknowledge receipt within 5 business days and aim to provide a fix
or mitigation timeline within 14 days for confirmed issues.

## Scope

In scope:

- The Copilot Session Manager application code in this repository
- Default configuration shipped with the app
- Any artifacts published to GitHub Releases under this org

Out of scope:

- The GitHub Copilot CLI itself (report to
  [github/copilot-cli](https://github.com/github/copilot-cli) or via GitHub)
- The GitHub platform (report to GitHub via their security program)
- Third-party dependencies (please report upstream first; we will help
  coordinate if needed)

## What we take seriously

- Reading or exfiltration of session content / prompts / code
- Privilege escalation through ConPTY hosting
- Injection through `events.jsonl` or other Copilot CLI files
- Local DPAPI key handling mistakes
- Unsafe deserialization of YAML, JSON, or markdown
- Any code that runs unprompted from another user's session data

## Safe harbor

We will not pursue legal action against good-faith researchers who:

- Make a reasonable effort to avoid privacy violations and service disruption
- Do not access more data than necessary to demonstrate the issue
- Give us reasonable time to respond before public disclosure
