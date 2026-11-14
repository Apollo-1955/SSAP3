#Requires -Version 5.1
<#
.SYNOPSIS
    Pre-submission checks on the built ssap3aap.sys and its sources: the
    image properties Memory Integrity (HVCI) and attestation signing depend on.

.DESCRIPTION
    Static checks only; they do not replace loading the driver under Driver
    Verifier on real hardware.

      PE image
        - x64, native subsystem, a driver image
        - DYNAMIC_BASE, NX_COMPAT and GUARD_CF set
        - SectionAlignment is one page (0x1000), as HVCI requires
        - no section that is both writable and executable (HVCI)
        - imports only from ntoskrnl.exe, hal.dll, WDFLDR.SYS and WMILIB.SYS
      Sources
        - no executable pool types (NonPagedPool, NonPagedPoolExecute, ...);
          only NonPagedPoolNx / POOL_FLAG_NON_PAGED
        - ExInitializeDriverRuntime(DrvRtPoolNxOptIn) is called

    Exit code 0 when every check passes. Prints one PASS/FAIL line per check.
#>
[CmdletBinding()]
param(
    # Default: driver\Ssap3Aap\bin-msvc\ssap3aap.sys (resolved below).
    [string]$Image,
    # Default: driver\Ssap3Aap (resolved below).
    [string]$SourceRoot
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults.
if (-not $Image) { $Image = Join-Path $PSScriptRoot 'Ssap3Aap\bin-msvc\ssap3aap.sys' }
if (-not $SourceRoot) { $SourceRoot = Join-Path $PSScriptRoot 'Ssap3Aap' }

$failures = 0
function Report([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $status = if ($Ok) { 'PASS' } else { 'FAIL' }
    if (-not $Ok) { $script:failures++ }
    Write-Output ('{0}  {1}{2}' -f $status, $Name, $(if ($Detail) { " ($Detail)" } else { '' }))
}

$bytes = [IO.File]::ReadAllBytes($Image)
function U16([int]$o) { [BitConverter]::ToUInt16($bytes, $o) }
function U32([int]$o) { [BitConverter]::ToUInt32($bytes, $o) }

if ($bytes.Length -lt 0x400 -or (U16 0) -ne 0x5A4D) { throw "$Image is not a PE file." }
$pe = [int](U32 0x3C)
if ((U32 $pe) -ne 0x00004550) { throw "$Image has no PE signature." }
$coff = $pe + 4
$machine = U16 $coff
$sectionCount = U16 ($coff + 2)
$optionalSize = U16 ($coff + 16)
$opt = $coff + 20
$magic = U16 $opt
Report 'x64 PE32+ image' ($machine -eq 0x8664 -and $magic -eq 0x20B) ('machine 0x{0:X4}, magic 0x{1:X3}' -f $machine, $magic)

$sectionAlignment = U32 ($opt + 32)
$subsystem = U16 ($opt + 68)
$dllCharacteristics = U16 ($opt + 70)
Report 'native subsystem' ($subsystem -eq 1) "subsystem $subsystem"
Report 'section alignment is one page (HVCI)' ($sectionAlignment -eq 0x1000) ('0x{0:X}' -f $sectionAlignment)
foreach ($flag in @(@{ Name = 'DYNAMIC_BASE'; Bit = 0x40 }, @{ Name = 'NX_COMPAT'; Bit = 0x100 }, @{ Name = 'GUARD_CF'; Bit = 0x4000 })) {
    Report "$($flag.Name) set" (($dllCharacteristics -band $flag.Bit) -ne 0)
}

# Sections.
$sectionTable = $opt + $optionalSize
$sections = @()
for ($i = 0; $i -lt $sectionCount; $i++) {
    $s = $sectionTable + 40 * $i
    $name = [Text.Encoding]::ASCII.GetString($bytes, $s, 8).TrimEnd([char]0)
    $sections += [pscustomobject]@{
        Name = $name
        VirtualSize = U32 ($s + 8)
        VirtualAddress = U32 ($s + 12)
        RawSize = U32 ($s + 16)
        RawPointer = U32 ($s + 20)
        Characteristics = U32 ($s + 36)
    }
}
$writableExecutable = @($sections | Where-Object { ($_.Characteristics -band 0x20000000) -ne 0 -and ($_.Characteristics -band 0x80000000) -ne 0 })
Report 'no writable+executable section (HVCI)' ($writableExecutable.Count -eq 0) (($sections | ForEach-Object { '{0}:0x{1:X8}' -f $_.Name, $_.Characteristics }) -join ' ')

function RvaToOffset([uint32]$rva) {
    foreach ($section in $sections) {
        $size = [Math]::Max($section.VirtualSize, $section.RawSize)
        if ($rva -ge $section.VirtualAddress -and $rva -lt $section.VirtualAddress + $size) {
            return [int]($rva - $section.VirtualAddress + $section.RawPointer)
        }
    }
    throw ('RVA 0x{0:X} is outside every section.' -f $rva)
}

# Imports (data directory 1).
$importRva = U32 ($opt + 112 + 8)
$imports = @()
if ($importRva -ne 0) {
    $descriptor = RvaToOffset $importRva
    while ($true) {
        $nameRva = U32 ($descriptor + 12)
        if ($nameRva -eq 0) { break }
        $nameOffset = RvaToOffset $nameRva
        $end = [Array]::IndexOf($bytes, [byte]0, $nameOffset)
        $imports += [Text.Encoding]::ASCII.GetString($bytes, $nameOffset, $end - $nameOffset)
        $descriptor += 20
    }
}
$allowed = @('ntoskrnl.exe', 'hal.dll', 'wdfldr.sys', 'wmilib.sys')
$unexpected = @($imports | Where-Object { $_.ToLowerInvariant() -notin $allowed })
Report 'imports only kernel, HAL and KMDF loader' ($unexpected.Count -eq 0) ($imports -join ', ')

# Sources.
# Files only: in Windows PowerShell 5.1, -Include with -LiteralPath also returns
# folders (such as bin-msvc), which Get-Content cannot read.
$code = Get-ChildItem -LiteralPath $SourceRoot -Recurse -File | Where-Object { $_.Extension -in '.c', '.h' } | ForEach-Object {
    $file = $_
    Get-Content -LiteralPath $file.FullName | ForEach-Object -Begin { $n = 0 } -Process { $n++; [pscustomobject]@{ File = $file.Name; Line = $n; Text = $_ } }
}
$executablePool = @($code | Where-Object { $_.Text -match '\b(NonPagedPool|NonPagedPoolExecute|NonPagedPoolMustSucceed|NonPagedPoolCacheAligned|POOL_FLAG_NON_PAGED_EXECUTE)\b' -and $_.Text -notmatch '^\s*(//|/\*|\*)' })
Report 'no executable pool types in sources' ($executablePool.Count -eq 0) (($executablePool | ForEach-Object { "$($_.File):$($_.Line)" }) -join ', ')
$nxOptIn = @($code | Where-Object { $_.Text -match 'ExInitializeDriverRuntime\s*\(\s*DrvRtPoolNxOptIn\s*\)' })
Report 'ExInitializeDriverRuntime(DrvRtPoolNxOptIn) called' ($nxOptIn.Count -gt 0)

Write-Output ('SHA-256 {0}' -f (Get-FileHash -LiteralPath $Image -Algorithm SHA256).Hash)
if ($failures -gt 0) { Write-Output "$failures check(s) failed."; exit 1 }
Write-Output 'All driver image checks passed.'
exit 0
