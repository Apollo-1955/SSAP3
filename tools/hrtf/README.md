# HRIR table builder (offline)

`HrirTableBuilder` turns the MIT KEMAR diffuse-field equalized HRTF set into
`audio/SSAP3Spatializer/ssap3_hrir_data.h`, which the spatial engine compiles
in. It runs on a developer PC only. It is not part of the app or the
installer, and nothing in `audiodg.exe` parses HRTF files.

## Source data

- MIT KEMAR HRTF measurements by W. G. Gardner and K. D. Martin, MIT Media
  Lab, 1994: https://sound.media.mit.edu/resources/KEMAR.html
- Archive used: `diffuse.zip` (the diffuse-field equalized set), SHA-256
  `A48E73137390626EF1E45D1DE6A4D98530C6AA570E71B40F7C4442241ED5FA22`.
  The hash is also written into the generated header.
- Licence: free use, with citation of the authors (see `THIRD_PARTY.md`).

The raw measurement files are **not** in this repository. Download the
archive yourself, check its SHA-256, and unpack it into an empty folder
outside the repository.

## Run

```powershell
dotnet run --project tools\hrtf\HrirTableBuilder\HrirTableBuilder.csproj -c Release -- `
    <unpacked diffuse folder> <path to diffuse.zip> audio\SSAP3Spatializer\ssap3_hrir_data.h
```

Expected output for the archive above:

```
Wrote audio\SSAP3Spatializer\ssap3_hrir_data.h: 368 directions x 2 ears x 96 taps; minimum retained energy 99.02 %; scale 3.77974E-05.
Right-side check at 0/90: right ear 14.8 dB louder, 30 samples earlier.
Max onset shift: 33 samples; ...
```

The output is deterministic: the same archive gives a byte-identical header.

## What it checks and does

1. Each WAV is PCM, stereo, 16-bit, 44.1 kHz and 128 frames, and is named
   `H<elevation>e<azimuth>a.wav`. There must be 368 files on 14 rings from
   -40° to +90°. Anything else stops the build.
2. **Convention check from the data.** At azimuth 90° the right ear must be
   more than 6 dB louder and earlier. Otherwise the tool refuses to guess
   which side is which.
3. Each ear's response is aligned to its onset (the first sample at 15 % of
   the peak, keeping 2 samples of lead-in). The removed shift is stored
   separately, so the engine can blend neighbouring directions without comb
   filtering and restore the exact interaural time difference.
4. Each response is truncated to 96 taps. The build stops if any response
   keeps less than 97 % of its energy (the actual minimum is 99.02 %).
5. One scale factor normalises the set to diffuse-field unit power.

The engine also checks the table's layout with `static_assert`s (ring order,
azimuth range, onset bounds against its input history), so a malformed
header fails to compile instead of misbehaving at run time.

## Using a different dataset

SOFA (netCDF-4/HDF5) import is not implemented. If you add it, put the
parser here, in this offline tool, and emit the same header layout. Never
put it in the engine (ADR-5 in `IMPLEMENTATION_ROADMAP.md`).
