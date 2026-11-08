using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace Toolbox.Windows.Shell;

/// <summary>
/// Share from the wheel. With Google's Quick Share for Windows installed, the files go straight to
/// its "send" window (the same way its own Explorer menu entry starts it); otherwise Windows' share
/// sheet opens (Nearby sharing, Phone Link and apps that accept files). Amir Lap itself sends
/// nothing: the user picks the device or app, and that app does the sending.
/// </summary>
internal static class ShareService
{
    public const string ActionId = "share";

    /// <summary>Where Quick Share for Windows installs itself.</summary>
    private static string QuickShareExe => IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "NearbyShare", "nearby_share.exe");

    public static bool QuickShareInstalled => File.Exists(QuickShareExe);

    /// <summary>The Share bubble's entry on the wheel.</summary>
    public static Wheel.WheelSegmentModel WheelModel(int fileCount)
    {
        bool quick = QuickShareInstalled;
        var title = quick ? "Quick Share" : Lang.T("Share", "مشاركة");
        var detail = quick
            ? Lang.T(fileCount == 1 ? "Send to a nearby phone or PC" : $"Send {fileCount} items nearby", fileCount == 1 ? "إرسال إلى هاتف أو حاسوب قريب" : $"إرسال {fileCount} عناصر إلى جهاز قريب")
            : Lang.T("Nearby sharing, Phone Link and apps", "المشاركة القريبة وPhone Link والتطبيقات");
        return new Wheel.WheelSegmentModel(ActionId, Lang.T("Share", "مشاركة"), title, "lap-share", fileCount > 0, detail);
    }

    public static void Share(IReadOnlyList<string> paths, Native.POINT? anchor, Action<string, string> problem)
    {
        var items = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (items.Count == 0)
        {
            problem(Lang.T("Nothing to share", "لا شيء للمشاركة"), Lang.T("The file is no longer there.", "الملف لم يعد موجودًا."));
            return;
        }
        if (QuickShareInstalled && TryQuickShare(items)) return;
        _ = ShowShareSheetAsync(items, anchor, problem);
    }

    private static bool TryQuickShare(IReadOnlyList<string> paths)
    {
        try
        {
            var start = new ProcessStartInfo(QuickShareExe) { UseShellExecute = false };
            start.ArgumentList.Add("--files-from=context-menu");
            foreach (var p in paths) start.ArgumentList.Add(p);
            Process.Start(start)?.Dispose();
            Log.Info($"Shared {paths.Count} item(s) with Quick Share.");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Quick Share could not be started, using the Windows share sheet: {ex.Message}");
            return false;
        }
    }

    // ---- Windows share sheet (documented desktop pattern: IDataTransferManagerInterop) ----------

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private static async Task ShowShareSheetAsync(IReadOnlyList<string> paths, Native.POINT? anchor, Action<string, string> problem)
    {
        Window? host = null;
        try
        {
            var items = new List<IStorageItem>();
            foreach (var p in paths)
                items.Add(File.Exists(p) ? await StorageFile.GetFileFromPathAsync(p) : await StorageFolder.GetFolderFromPathAsync(p));

            // The sheet needs a window of ours to belong to: a tiny invisible one where the file was dropped.
            host = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                Width = 2,
                Height = 2,
                Title = Lang.T("Share", "مشاركة"),
            };
            host.Show();
            var hwnd = new WindowInteropHelper(host).Handle;
            if (anchor is { } a) Native.SetWindowPos(hwnd, IntPtr.Zero, a.X, a.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            Foreground.Activate(host);

            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DataTransferManagerIid;
            var manager = MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, ref iid));
            var owner = host;
            void Done() => ToolboxHost.OnUi(() => { if (owner.IsLoaded) owner.Close(); });
            manager.DataRequested += (_, args) =>
            {
                var data = args.Request.Data;
                data.Properties.Title = paths.Count == 1 ? IOPath.GetFileName(paths[0]) : Lang.T($"{paths.Count} items", $"{paths.Count} عناصر");
                data.RequestedOperation = DataPackageOperation.Copy;
                data.SetStorageItems(items);
                data.ShareCompleted += (_, _) => Done();
                data.ShareCanceled += (_, _) => Done();
            };
            interop.ShowShareUIForWindow(hwnd);
            Log.Info($"Opened the Windows share sheet for {paths.Count} item(s).");

            // If the sheet never reports back, don't leave the helper window behind for long.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            timer.Tick += (_, _) => { timer.Stop(); Done(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"Share sheet failed: {ex.Message}");
            host?.Close();
            problem(Lang.T("Couldn't open sharing", "تعذّر فتح المشاركة"), ex.Message);
        }
    }
}
