# Contributing to SSAP3

Contributions are welcome, especially:

- validation on more Windows 10/11 builds, Bluetooth adapters, and AirPods;
- reconnect and transport reliability;
- safer production driver packaging/signing;
- DSP improvements with objective tests and listening notes;
- accessibility and localization;
- protocol parser tests and documentation.

## Before opening a pull request

1. Open an issue for large behavioral or driver changes.
2. Keep each pull request focused.
3. Do not include device addresses, Bluetooth keys, private certificates,
   PFX files, crash dumps, or personal diagnostics.
4. Preserve the GPL-compatible provenance of protocol work.
5. Run:

   ```powershell
   dotnet build .\src\SSAP3.App\SSAP3.App.csproj -c Release
   dotnet run --project .\tests\SSAP3.Core.SmokeTests\SSAP3.Core.SmokeTests.csproj -c Release
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\audio\build-spatializer.ps1
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\audio\test-spatializer.ps1
   ```

6. Describe the hardware and Windows build used for testing.

## Driver changes

Driver pull requests need extra care:

- explain the device/transport problem being solved;
- keep IOCTL inputs versioned and bounded;
- validate caller session ownership;
- avoid expanding the INF binding beyond the Apple AAP profile without a
  separate design discussion;
- document install, rollback, and failure behavior;
- never add a production-trusted signature to experimental code.

## DSP changes

Audio changes should include:

- the intended perceptual result;
- exact parameter or algorithm changes;
- smoke-test results;
- confirmation that Off remains bit-exact;
- confirmation that `audiodg.exe` remains stable during real playback.

Avoid effects that achieve an obvious difference only by changing loudness.

## License

By contributing, you agree that your contribution is licensed under the
repository's GPL-3.0-or-later license.
