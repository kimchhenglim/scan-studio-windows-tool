using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Devices.Scanners;
using Windows.Storage;

namespace ScanStudio;

public enum ColorMode { Color, Grayscale, BlackAndWhite }

public sealed record ScannerInfo(string DeviceId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Scans through the modern Windows scanning API (Windows.Devices.Scanners), the same API
/// the Windows Scan app uses. It talks to the driver over WIA 2.0, which works with drivers
/// that reject the legacy WIA Automation (wiaaut.dll) transfer path.
/// </summary>
public static class ScannerService
{
    // One ImageScanner per device for the app's lifetime. Opening a new one per scan leaves the previous
    // session holding the device until the GC finalizes it, and the driver then reports "device is busy".
    private static readonly Dictionary<string, ImageScanner> Cache = new();

    private static async Task<ImageScanner> GetScannerAsync(string deviceId)
    {
        if (Cache.TryGetValue(deviceId, out var cached)) return cached;
        var scanner = await ImageScanner.FromIdAsync(deviceId)
            ?? throw new InvalidOperationException("The selected scanner is not available. Check that it is on and connected, then click Refresh.");
        Cache[deviceId] = scanner;
        return scanner;
    }

    public static async Task<List<ScannerInfo>> GetScannersAsync()
    {
        Cache.Clear(); // Refresh: reconnect in case the device was unplugged or replugged.
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.ImageScanner);
        return devices.Select(d => new ScannerInfo(d.Id, d.Name)).ToList();
    }

    /// <summary>
    /// Scans one page and moves the result to <paramref name="outputPathWithoutExtension"/> plus the file's extension.
    /// Returns the file path and the resolution the scanner actually used (drivers may round the request).
    /// </summary>
    public static async Task<(string Path, int ActualDpi)> ScanPageAsync(string deviceId, ColorMode mode, int dpi, string workFolder,
        string outputPathWithoutExtension, CancellationToken cancel, Action<int> onWaiting = null)
    {
        var scanner = await GetScannerAsync(deviceId);

        // Prefer the flatbed; fall back to the feeder, then whatever the device defaults to.
        ImageScannerScanSource source;
        IImageScannerSourceConfiguration config;
        if (scanner.IsScanSourceSupported(ImageScannerScanSource.Flatbed))
        {
            source = ImageScannerScanSource.Flatbed;
            config = scanner.FlatbedConfiguration;
        }
        else if (scanner.IsScanSourceSupported(ImageScannerScanSource.Feeder))
        {
            source = ImageScannerScanSource.Feeder;
            var feeder = scanner.FeederConfiguration;
            feeder.MaxNumberOfPages = 1;
            config = feeder;
        }
        else
        {
            source = ImageScannerScanSource.Default;
            config = null;
        }
        int actualDpi = dpi;

        if (config != null)
        {
            var colorMode = mode switch
            {
                ColorMode.Color => ImageScannerColorMode.Color,
                ColorMode.Grayscale => ImageScannerColorMode.Grayscale,
                _ => ImageScannerColorMode.Monochrome
            };
            if (!config.IsColorModeSupported(colorMode))
                throw new InvalidOperationException($"This scanner does not support the {mode} color mode.");
            config.ColorMode = colorMode;

            int min = (int)config.MinResolution.DpiX, max = (int)config.MaxResolution.DpiX;
            if (dpi < min || dpi > max)
                throw new InvalidOperationException($"This scanner supports {min}–{max} DPI. Choose a resolution in that range.");
            config.DesiredResolution = new ImageScannerResolution { DpiX = dpi, DpiY = dpi };
            actualDpi = (int)Math.Round(config.ActualResolution.DpiX);

            // Uncompressed bitmap is the one format every driver supports; we convert when saving.
            if (config.IsFormatSupported(ImageScannerFormat.DeviceIndependentBitmap))
                config.Format = ImageScannerFormat.DeviceIndependentBitmap;
            else if (config.IsFormatSupported(ImageScannerFormat.Png))
                config.Format = ImageScannerFormat.Png;
            else if (config.IsFormatSupported(ImageScannerFormat.Tiff))
                config.Format = ImageScannerFormat.Tiff;
        }

        // Each scan goes into its own empty folder so we know exactly which file it produced.
        string scanDir = Path.Combine(workFolder, "incoming_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scanDir);
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(scanDir);

            // Some scanners refuse a new scan while the head returns after the previous page.
            // (Measured on an HP Deskjet F2200: busy for ~15–20 s after every page.)
            // Keep retrying for up to a minute instead of failing when pages are scanned back-to-back.
            ImageScannerScanResult result = null;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    result = await scanner.ScanFilesToFolderAsync(source, folder).AsTask(cancel);
                    break;
                }
                catch (Exception ex) when (IsTransientBusy(ex) && waited.Elapsed < TimeSpan.FromSeconds(60)
                                           && !cancel.IsCancellationRequested)
                {
                    onWaiting?.Invoke((int)waited.Elapsed.TotalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancel);
                }
            }

            var file = result.ScannedFiles.FirstOrDefault()
                ?? throw new InvalidOperationException("The scanner finished but returned no image. Check that a page is on the glass and try again.");

            string target = outputPathWithoutExtension + Path.GetExtension(file.Path).ToLowerInvariant();
            File.Move(file.Path, target, overwrite: true);
            return (target, actualDpi);
        }
        finally
        {
            try { Directory.Delete(scanDir, true); } catch { }
        }
    }

    /// <summary>Errors meaning "not ready yet": busy, warming up, or a call made before the device settled.</summary>
    private static bool IsTransientBusy(Exception ex) =>
        (uint)ex.HResult is 0x80210006 or 0x8021000A or 0x80000019;

    public static string DescribeError(Exception ex)
    {
        if (ex is OperationCanceledException) return "Scan cancelled.";
        if (ex is InvalidOperationException) return ex.Message;
        uint hr = (uint)ex.HResult;
        return hr switch
        {
            0x80210003 => "There is no paper in the document feeder.",
            0x80210005 => "The scanner is offline. Make sure it is powered on and connected.",
            0x80210006 or 0x80000019 => "The scanner stayed busy for over a minute. Make sure no other app (such as Windows Scan) is using it, then try again.",
            0x8021000A => "The scanner is warming up. Try again in a few seconds.",
            0x80210015 => "No scanner is available. Check the connection and click Refresh.",
            0x80070057 => "The scanner rejected one of the settings. Try a different resolution or color mode.",
            _ => $"{ex.Message.Trim()} (0x{hr:X8})"
        };
    }
}
