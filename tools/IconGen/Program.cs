using Toolbox.Windows.Shell;

// Usage: IconGen [output.ico]   (default: src/Toolbox.Windows/app.ico, found from the repo root)
// WPF rendering needs a single-threaded apartment.
int exitCode = 1;
var worker = new Thread(() =>
{
    var path = args.Length > 0 ? Path.GetFullPath(args[0]) : DefaultPath();
    var bytes = AppIcon.IcoBytes(AppIcon.BrandAccent, [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]);
    File.WriteAllBytes(path, bytes);
    Console.WriteLine($"Wrote {path} ({bytes.Length:N0} bytes).");
    exitCode = 0;
});
worker.SetApartmentState(ApartmentState.STA);
worker.Start();
worker.Join();
return exitCode;

static string DefaultPath()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "FileToolbox.sln")))
            return Path.Combine(d.FullName, "src", "Toolbox.Windows", "app.ico");
    throw new DirectoryNotFoundException("Run from inside the repository or pass an output path.");
}