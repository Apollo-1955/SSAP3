# SSAP3 installer

| File | Purpose |
| --- | --- |
| `SSAP3.iss` | Inno Setup script: UI, optional tasks, file copy to `%ProgramFiles%\SSAP3`, uninstaller entry |
| `Install-SSAP3.ps1` | Elevated system-configuration step, run by Setup from `%ProgramFiles%\SSAP3\setup` |
| `Uninstall-SSAP3.ps1` | Reverts every recorded change. Idempotent. `-RemoveUserData` for a complete uninstall |
| `Ssap3Setup.psm1` | Shared engine: manifest, protected ACLs, registry record/restore, driver and spatial-engine steps, undo |
| `Test-Ssap3SystemState.ps1` | Read-only before/after integrity verifier |
| `build-installer.ps1` | Builds `artifacts\SSAP3-Setup-<version>.exe`, package hashes and the SBOM |

Policy: no Test Mode, BCD, Secure Boot, BitLocker or certificate changes.
The AAP driver is included only from a Microsoft-signed package. The
system-wide audio engine is opt-in.

User-facing instructions are in [docs/INSTALL.md](../docs/INSTALL.md).
