using System.Runtime.InteropServices;

// The app runs elevated: every P/Invoke here targets a system DLL, so load
// them from System32 only — never a planted copy beside the app or on PATH.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
