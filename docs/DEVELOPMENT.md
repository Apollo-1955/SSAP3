# Developing SoundStage

## Toolchain

- x64 Windows 10 build 19041+ or Windows 11;
- Git;
- .NET 8 SDK;
- Visual Studio 2022 Build Tools with MSVC x64;
- Windows 10/11 SDK;
- Windows Driver Kit with KMDF 1.31 headers and libraries for the driver;
- Inno Setup 6 for the single-EXE installer;
- PowerShell 5.1 or newer.

The source tree is deterministic where practical and treats managed compiler
warnings as errors.

## Build the managed application

```powershell
dotnet restore .\src\SoundStage.App\SoundStage.App.csproj
dotnet build .\src\SoundStage.App\SoundStage.App.csproj -c Release
```

Create a self-contained single-file build:

```powershell
dotnet publish .\src\SoundStage.App\SoundStage.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o .\artifacts\app `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:DebugType=None `
  /p:DebugSymbols=false
```

## Generate brand assets

The committed PNG and multi-resolution ICO can be regenerated on Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\assets\build-icons.ps1
```

`assets/SoundStage.svg` is the editable vector source.
GDI+ rasterization can differ slightly between Windows builds, so CI validates
the generated formats and dimensions instead of requiring byte-identical PNG
and ICO output across runner images.

## Build and test the spatializer

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\audio\build-spatializer.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\audio\test-spatializer.ps1
```

The smoke harness verifies:

- Off is bit-exact;
- Fixed changes the stereo signal;
- tracked yaw changes the HRTF result.

The VST uses a small local ABI declaration instead of bundling the proprietary
Steinberg VST2 SDK.

## Run managed smoke tests

```powershell
dotnet run `
  --project .\tests\SoundStage.Core.SmokeTests\SoundStage.Core.SmokeTests.csproj `
  -c Release
```

These cover AACP packet layouts, advertisement/battery parsers, crypto helpers,
profile advice, connection state, and motion frame parsing.

## Build the driver

Install the required Visual Studio/WDK components, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\driver\build-driver-msvc.ps1
```

The LLVM script is useful for compiler diagnostics, but the packaged driver
should be built with the supported MSVC/WDK path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\driver\build-driver-clang.ps1
```

Generate a local development certificate only on a trusted development
machine:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\driver\New-TestSigningCertificate.ps1
```

Never commit a private key or PFX. The repository ignores common private-key
extensions. A public CER used to verify an already packaged test build is not a
private signing key.

Package the signed driver:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\driver\package-driver.ps1
```

## Diagnostics

Build and run the read-only diagnostics collector:

```powershell
dotnet run --project .\src\SoundStage.Diagnostics\SoundStage.Diagnostics.csproj `
  -c Release -- .\artifacts\diagnostics
```

The transport probe directly exercises the user-mode driver client:

```powershell
dotnet run --project .\src\SoundStage.TransportProbe\SoundStage.TransportProbe.csproj `
  -c Release
```

Do not include Bluetooth keys, device addresses, or diagnostic archives in
public issues without reviewing them first.

## Packaging checklist

Build and verify the complete installer:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\installer\build-installer.ps1 `
  -Version 0.3.0
```

The script regenerates icons, builds and tests the managed core and spatializer,
builds the MSVC/WDK driver, creates or reuses a local test-signing certificate,
signs and verifies SYS/CAT, publishes the self-contained app, writes the payload
manifest, and compiles `SoundStage-Setup.exe` with Inno Setup.

Before publishing:

1. Test first-phase install, restart, second-phase install, repair, migration,
   and uninstall on a disposable test machine.
2. Verify the release EXE hash.
3. Mark every test-signed build as a prerelease.

## Coding guidelines

- Keep protocol parsing out of kernel mode.
- Validate every packet length before indexing.
- Do not allocate, lock, access the registry, or call managed code in the DSP
  audio callback.
- Preserve original endpoint effects and make install steps reversible.
- Keep the UI English and do not add silent network activity.
- Add a smoke test for protocol or parser changes.
