# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub's **Report a
vulnerability** button (Security Advisories) on this repository. Do not open a
public issue and do not publish a working exploit before a fix is available.

## Project commitments

- Production setup never weakens Windows. It does not turn off Secure Boot,
  Test Mode, Memory Integrity, Defender or driver-signature enforcement, and
  it does not add certificates to Windows trust stores.
- Kernel code ships only with a Microsoft signature (attestation or WHQL).
- No telemetry, analytics or network access in the app, driver or audio
  engine.
- The repository must never contain private keys, PFX files, tokens or
  personal diagnostics. CI runs secret scanning.
- Critical and high-severity findings block releases.

`SECURITY_AUDIT.md` lists the findings in the inherited code.
`SECURITY_REPORT.md` records what was checked, what was fixed, and what
remains open. SSAP3 does not claim to be free of vulnerabilities, and no
software can promise immunity to unknown (zero-day) flaws.
