using System.IO;
using System.Text;
using System.Text.Json;
using NegarestanCAD.Models;

namespace NegarestanCAD.Services;

/// <summary>
/// ذخیره و بازیابی پایدار تنظیمات برنامه در پوشه LocalAppData کاربر
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string AppDataDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NegarestanCAD");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // در صورت خراب بودن فایل تنظیمات، تنظیمات پیش‌فرض بارگذاری می‌شود
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
        }
        catch
        {
            // جلوگیری از کرش در صورت محدودیت دسترسی فایل
        }
    }
}
