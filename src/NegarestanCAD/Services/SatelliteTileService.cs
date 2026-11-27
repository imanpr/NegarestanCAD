using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using NegarestanCAD.Models;

namespace NegarestanCAD.Services;

/// <summary>
/// سرویس دریافت، کش حافظه و کش دیسک تایل‌های نقشه ماهواره‌ای (Google Hybrid/Satellite، Esri World Imagery، OpenStreetMap)
/// </summary>
public static class SatelliteTileService
{
    private const double EarthRadius = 6378137.0;
    private const double InitialResolution = 156543.03392804097; // متر بر پیکسل در زوم 0 روی استوا
    private const int MaxMemoryTiles = 450;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, BitmapImage> MemoryCache = new();
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();
    private static readonly SemaphoreSlim DownloadGate = new(8, 8);

    public static string TileCacheRoot
    {
        get
        {
            string dir = Path.Combine(SettingsService.AppDataDirectory, "TileCache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NegarestanCAD/1.2 (Windows WPF Surveying App)");
        return client;
    }

    public static int GetMaxZoom(SatelliteMapProvider provider) => provider switch
    {
        SatelliteMapProvider.GoogleHybrid => 20,
        SatelliteMapProvider.GoogleSatellite => 20,
        SatelliteMapProvider.EsriWorldImagery => 19,
        SatelliteMapProvider.OpenStreetMap => 19,
        _ => 19
    };

    public static string GetProviderDisplayName(SatelliteMapProvider provider) => provider switch
    {
        SatelliteMapProvider.GoogleHybrid => "Google ماهواره + برچسب فارسی",
        SatelliteMapProvider.GoogleSatellite => "Google ماهواره خالص",
        SatelliteMapProvider.EsriWorldImagery => "Esri World Imagery (ماهواره)",
        SatelliteMapProvider.OpenStreetMap => "OpenStreetMap (خیابان)",
        _ => provider.ToString()
    };

    public static string GetAttributionText(SatelliteMapProvider provider) => provider switch
    {
        SatelliteMapProvider.GoogleHybrid => "Imagery © Google (Hybrid)",
        SatelliteMapProvider.GoogleSatellite => "Imagery © Google",
        SatelliteMapProvider.EsriWorldImagery => "Imagery © Esri, Maxar, Earthstar Geographics",
        SatelliteMapProvider.OpenStreetMap => "© OpenStreetMap contributors",
        _ => string.Empty
    };

    public static string BuildTileUrl(SatelliteMapProvider provider, int z, int x, int y)
    {
        int sub = Math.Abs((x + y) % 4);
        return provider switch
        {
            SatelliteMapProvider.GoogleHybrid => $"https://mt{sub}.google.com/vt/lyrs=y&x={x}&y={y}&z={z}&hl=fa",
            SatelliteMapProvider.GoogleSatellite => $"https://mt{sub}.google.com/vt/lyrs=s&x={x}&y={y}&z={z}",
            SatelliteMapProvider.EsriWorldImagery => $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
            SatelliteMapProvider.OpenStreetMap => $"https://tile.openstreetmap.org/{z}/{x}/{y}.png",
            _ => $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
        };
    }

    #region ریاضیات Web Mercator (EPSG:3857)

    public static (double px, double py) LatLonToWorldPixels(double latDeg, double lonDeg, double zoom)
    {
        double clampedLat = Math.Clamp(latDeg, -85.05112878, 85.05112878);
        double sinLat = Math.Sin(clampedLat * Math.PI / 180.0);
        double mapSize = 256.0 * Math.Pow(2.0, zoom);

        double px = ((lonDeg + 180.0) / 360.0) * mapSize;
        double py = (0.5 - Math.Log((1.0 + sinLat) / (1.0 - sinLat)) / (4.0 * Math.PI)) * mapSize;
        return (px, py);
    }

    public static (double lat, double lon) WorldPixelsToLatLon(double px, double py, double zoom)
    {
        double mapSize = 256.0 * Math.Pow(2.0, zoom);
        double x = (px / mapSize) - 0.5;
        double y = 0.5 - (py / mapSize);

        double lat = 90.0 - 360.0 * Math.Atan(Math.Exp(-y * 2.0 * Math.PI)) / Math.PI;
        double lon = 360.0 * x;
        return (Math.Clamp(lat, -85.05112878, 85.05112878), Math.Clamp(lon, -180.0, 180.0));
    }

    public static double MetersPerPixel(double latDeg, double zoom)
    {
        double cosLat = Math.Cos(Math.Clamp(latDeg, -85.0, 85.0) * Math.PI / 180.0);
        return (InitialResolution * cosLat) / Math.Pow(2.0, zoom);
    }

    public static double ZoomFromMetersPerPixel(double latDeg, double metersPerPixel)
    {
        if (metersPerPixel <= 1e-6) return 20.0;
        double cosLat = Math.Cos(Math.Clamp(latDeg, -85.0, 85.0) * Math.PI / 180.0);
        double z = Math.Log2((InitialResolution * cosLat) / metersPerPixel);
        return Math.Clamp(z, 2.0, 20.5);
    }

    #endregion

    private static string GetCacheKey(SatelliteMapProvider provider, int z, int x, int y)
        => $"{provider}_{z}_{x}_{y}";

    private static string GetDiskPath(SatelliteMapProvider provider, int z, int x, int y)
        => Path.Combine(TileCacheRoot, provider.ToString(), z.ToString(), $"{x}_{y}.tile");

    /// <summary>تلاش برای دریافت فوری تایل از حافظه یا دیسک؛ در صورت نبود، دانلود ناهمزمان شروع می‌شود</summary>
    public static BitmapImage? TryGetTile(SatelliteMapProvider provider, int z, int x, int y, Action? onDownloaded = null)
    {
        int maxTile = 1 << z;
        if (z < 0 || y < 0 || y >= maxTile) return null;
        x = ((x % maxTile) + maxTile) % maxTile;

        string key = GetCacheKey(provider, z, x, y);
        if (MemoryCache.TryGetValue(key, out var cached))
            return cached;

        // تلاش برای خواندن از کش دیسک
        string diskPath = GetDiskPath(provider, z, x, y);
        if (File.Exists(diskPath))
        {
            var fromDisk = LoadBitmapFromBytes(SafeReadAllBytes(diskPath));
            if (fromDisk != null)
            {
                AddToMemoryCache(key, fromDisk);
                return fromDisk;
            }
        }

        // شروع دانلود غیرمسدودکننده در پس‌زمینه
        if (InFlight.TryAdd(key, 1))
        {
            _ = Task.Run(async () =>
            {
                await DownloadGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    string url = BuildTileUrl(provider, z, x, y);
                    byte[] bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                    var bmp = LoadBitmapFromBytes(bytes);
                    if (bmp != null)
                    {
                        AddToMemoryCache(key, bmp);
                        SaveToDiskQuietly(diskPath, bytes);
                        onDownloaded?.Invoke();
                    }
                }
                catch
                {
                    // در حالت آفلاین یا خطای شبکه، بدون کرش رد می‌شود
                }
                finally
                {
                    DownloadGate.Release();
                    InFlight.TryRemove(key, out _);
                }
            });
        }

        return null;
    }

    /// <summary>دریافت همگام تایل‌ها برای پرینت رسمی (با تایم‌اوت کوتاه تا چاپ هرگز قفل نشود)</summary>
    public static BitmapImage? GetTileForPrint(SatelliteMapProvider provider, int z, int x, int y)
    {
        int maxTile = 1 << z;
        if (z < 0 || y < 0 || y >= maxTile) return null;
        x = ((x % maxTile) + maxTile) % maxTile;

        string key = GetCacheKey(provider, z, x, y);
        if (MemoryCache.TryGetValue(key, out var cached))
            return cached;

        string diskPath = GetDiskPath(provider, z, x, y);
        if (File.Exists(diskPath))
        {
            var fromDisk = LoadBitmapFromBytes(SafeReadAllBytes(diskPath));
            if (fromDisk != null)
            {
                AddToMemoryCache(key, fromDisk);
                return fromDisk;
            }
        }

        try
        {
            string url = BuildTileUrl(provider, z, x, y);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            byte[] bytes = Http.GetByteArrayAsync(url, cts.Token).GetAwaiter().GetResult();
            var bmp = LoadBitmapFromBytes(bytes);
            if (bmp != null)
            {
                AddToMemoryCache(key, bmp);
                SaveToDiskQuietly(diskPath, bytes);
                return bmp;
            }
        }
        catch { }

        return null;
    }

    public static long GetDiskCacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(TileCacheRoot)) return 0;
            return Directory.EnumerateFiles(TileCacheRoot, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    public static void ClearCache()
    {
        MemoryCache.Clear();
        try
        {
            if (Directory.Exists(TileCacheRoot))
                Directory.Delete(TileCacheRoot, recursive: true);
            Directory.CreateDirectory(TileCacheRoot);
        }
        catch { }
    }

    private static void AddToMemoryCache(string key, BitmapImage bmp)
    {
        if (MemoryCache.Count >= MaxMemoryTiles)
        {
            foreach (var oldKey in MemoryCache.Keys.Take(120))
                MemoryCache.TryRemove(oldKey, out _);
        }
        MemoryCache[key] = bmp;
    }

    private static BitmapImage? LoadBitmapFromBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 64) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static byte[]? SafeReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return null; }
    }

    private static void SaveToDiskQuietly(string path, byte[] bytes)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }
        catch { }
    }
}
