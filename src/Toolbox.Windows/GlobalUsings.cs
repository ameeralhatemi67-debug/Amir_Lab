// WPF projects do not import System.IO implicitly, and System.IO.Path collides with
// System.Windows.Shapes.Path. File-system types are aliased here; use IOPath for paths.
global using Directory = System.IO.Directory;
global using File = System.IO.File;
global using FileFormatException = System.IO.FileFormatException;
global using FileInfo = System.IO.FileInfo;
global using FileStream = System.IO.FileStream;
global using IOException = System.IO.IOException;
global using IOPath = System.IO.Path;
global using MemoryStream = System.IO.MemoryStream;
