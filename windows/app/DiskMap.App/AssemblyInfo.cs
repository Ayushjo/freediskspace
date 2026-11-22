using System.Runtime.InteropServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The app runs as administrator: a native library it imports by name comes from System32, never
// from the app's or the working folder, where a planted copy could run elevated.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
