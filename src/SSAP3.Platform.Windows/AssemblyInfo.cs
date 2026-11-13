using System.Runtime.InteropServices;

// DLL-planting defence: every P/Invoke in this assembly resolves its native
// library from %SystemRoot%\System32 only, never from the application or
// current directory.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
