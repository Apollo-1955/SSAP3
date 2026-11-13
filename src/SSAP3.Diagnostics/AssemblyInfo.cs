using System.Runtime.InteropServices;

// DLL-planting defence: native libraries resolve from System32 only.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
