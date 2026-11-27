using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DeskBox.Platform;

/// <summary>
/// Prepares dropped files for the engine's scan pipeline. The Go engine decodes
/// JPEG/PNG/GIF itself; HEIC (iPhone), BMP, TIFF and WebP are transcoded here
/// through WIC into a temp JPEG. Lives in Platform because it writes and deletes
/// files (module-boundary contract).
/// </summary>
internal static class ScanInputs
{
    private const int MaxPixels = 60 * 1000 * 1000;

    private static string TempRoot =>
        Path.Combine(Path.GetTempPath(), "deskbox-scan");

    public static async Task<(List<string> Ready, List<string> Temp, List<string> Failures)> PrepareAsync(
        IReadOnlyList<string> paths)
    {
        var ready = new List<string>();
        var temp = new List<string>();
        var failures = new List<string>();

        foreach (string path in paths)
        {
            if (IsDirect(path))
            {
                ready.Add(path);
                continue;
            }

            string? converted = await TranscodeAsync(path);
            if (converted is null)
            {
                failures.Add(Path.GetFileName(path) + "（无法解码，请先转成 JPG）");
                continue;
            }

            ready.Add(converted);
            temp.Add(converted);
        }

        return (ready, temp, failures);
    }

    public static void Cleanup(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception)
            {
                // Temp files are disposable; a locked one must not fail the scan.
            }
        }
    }

    private static bool IsDirect(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
            case ".png":
            case ".gif":
                return true;
            default:
                return false;
        }
    }

    private static async Task<string?> TranscodeAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using Windows.Storage.Streams.IRandomAccessStream input = await file.OpenReadAsync();

            // Plain CreateAsync(stream) deliberately: it runs codec arbitration,
            // which is what picks up the HEIF extension when it is installed.
            Windows.Graphics.Imaging.BitmapDecoder decoder =
                await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 ||
                (long)decoder.PixelWidth * decoder.PixelHeight > MaxPixels)
            {
                return null;
            }

            using Windows.Graphics.Imaging.SoftwareBitmap bitmap =
                await decoder.GetSoftwareBitmapAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    new Windows.Graphics.Imaging.BitmapTransform(),
                    Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

            using var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            Windows.Graphics.Imaging.BitmapEncoder encoder =
                await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, memory);
            encoder.SetSoftwareBitmap(bitmap);

            var propertySet = new Windows.Graphics.Imaging.BitmapPropertySet();
            propertySet.Add("ImageQuality", new Windows.Graphics.Imaging.BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single));
            await encoder.BitmapProperties.SetPropertiesAsync(propertySet);
            await encoder.FlushAsync();

            if (memory.Size == 0)
            {
                return null;
            }

            if (memory.Size > 256L * 1024 * 1024)
            {
                return null;
            }

            var bytes = new byte[(int)memory.Size];
            memory.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(memory.GetInputStreamAt(0));
            await reader.LoadAsync((uint)memory.Size);
            reader.ReadBytes(bytes);

            Directory.CreateDirectory(TempRoot);
            string dest = Path.Combine(TempRoot, Guid.NewGuid().ToString("N") + ".jpg");
            File.WriteAllBytes(dest, bytes);
            return dest;
        }
        catch (Exception ex)
        {
            App.Log($"[Scan] transcode {Path.GetFileName(path)} failed: {ex.Message}");
            return null;
        }
    }
}
