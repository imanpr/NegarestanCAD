using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NegarestanCAD.Models;
using NegarestanCAD.Services;

namespace NegarestanCAD.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public bool StartupCadPathChanged { get; private set; }
    private readonly string _initialCadPath;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _initialCadPath = settings.DefaultStartupCadPath ?? string.Empty;

        for (int z = 1; z <= 60; z++)
            CmbDefaultZone.Items.Add(z);

        LoadToUi();
    }

    private void LoadToUi()
    {
        TxtOrgName.Text = _settings.DefaultOrganizationName;
        TxtLicense.Text = _settings.DefaultLicenseNumber;
        TxtLogoPath.Text = _settings.DefaultLogoPath;
        UpdateLogoPreview(_settings.DefaultLogoPath);

        TxtStartupCadPath.Text = _settings.DefaultStartupCadPath;
        CmbCanvasTheme.SelectedIndex = _settings.IsDarkCanvas ? 0 : 1;

        CmbViewMode.SelectedIndex = _settings.DefaultViewMode switch
        {
            WorkspaceViewMode.SplitBoth => 0,
            WorkspaceViewMode.CadOnly => 1,
            WorkspaceViewMode.SatelliteOnly => 2,
            _ => 0
        };

        CmbSatProvider.SelectedIndex = _settings.DefaultSatelliteProvider switch
        {
            SatelliteMapProvider.GoogleHybrid => 0,
            SatelliteMapProvider.GoogleSatellite => 1,
            SatelliteMapProvider.EsriWorldImagery => 2,
            SatelliteMapProvider.OpenStreetMap => 3,
            _ => 0
        };

        CmbDefaultZone.SelectedItem = Math.Clamp(_settings.DefaultUtmZone, 1, 60);
        TxtDefaultLat.Text = _settings.DefaultMapLatitude.ToString("F7", CultureInfo.InvariantCulture);
        TxtDefaultLon.Text = _settings.DefaultMapLongitude.ToString("F7", CultureInfo.InvariantCulture);
        TxtDefaultZoom.Text = _settings.DefaultMapZoom.ToString("F1", CultureInfo.InvariantCulture);

        RefreshCacheSizeText();
    }

    private void UpdateLogoPreview(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                ImgLogoPreview.Source = bmp;
                return;
            }
            catch { }
        }
        ImgLogoPreview.Source = null;
    }

    private void RefreshCacheSizeText()
    {
        double mb = SatelliteTileService.GetDiskCacheSizeBytes() / (1024.0 * 1024.0);
        TxtCacheInfo.Text = $"حجم کش تصاویر ماهواره‌ای روی دیسک: {mb:F2} MB";
    }

    private void BtnBrowseLogo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "انتخاب لوگوی پیش‌فرض سازمان",
            Filter = "تصاویر (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dlg.ShowDialog() == true)
        {
            TxtLogoPath.Text = dlg.FileName;
            UpdateLogoPreview(dlg.FileName);
        }
    }

    private void BtnClearLogo_Click(object sender, RoutedEventArgs e)
    {
        TxtLogoPath.Text = string.Empty;
        UpdateLogoPreview(null);
    }

    private void BtnBrowseCad_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "انتخاب فایل نقشه DWG / DXF پیش‌فرض در شروع برنامه",
            Filter = "فایل‌های نقشه اتوکد (*.dwg;*.dxf)|*.dwg;*.dxf"
        };
        if (dlg.ShowDialog() == true)
            TxtStartupCadPath.Text = dlg.FileName;
    }

    private void BtnClearCad_Click(object sender, RoutedEventArgs e)
    {
        TxtStartupCadPath.Text = string.Empty;
    }

    private void BtnClearCache_Click(object sender, RoutedEventArgs e)
    {
        SatelliteTileService.ClearCache();
        RefreshCacheSizeText();
        MessageBox.Show("کش تصاویر ماهواره‌ای پاک‌سازی شد.", "پاک‌سازی کش", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.DefaultOrganizationName = TxtOrgName.Text.Trim();
        _settings.DefaultLicenseNumber = TxtLicense.Text.Trim();
        _settings.DefaultLogoPath = TxtLogoPath.Text.Trim();
        _settings.DefaultStartupCadPath = TxtStartupCadPath.Text.Trim();
        _settings.IsDarkCanvas = CmbCanvasTheme.SelectedIndex != 1;

        _settings.DefaultViewMode = CmbViewMode.SelectedIndex switch
        {
            1 => WorkspaceViewMode.CadOnly,
            2 => WorkspaceViewMode.SatelliteOnly,
            _ => WorkspaceViewMode.SplitBoth
        };

        _settings.DefaultSatelliteProvider = CmbSatProvider.SelectedIndex switch
        {
            1 => SatelliteMapProvider.GoogleSatellite,
            2 => SatelliteMapProvider.EsriWorldImagery,
            3 => SatelliteMapProvider.OpenStreetMap,
            _ => SatelliteMapProvider.GoogleHybrid
        };

        if (CmbDefaultZone.SelectedItem is int z)
            _settings.DefaultUtmZone = z;

        if (double.TryParse(PersianTextHelper.ToEnglishDigits(TxtDefaultLat.Text.Trim()), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) && lat is >= -85 and <= 85)
            _settings.DefaultMapLatitude = lat;
        if (double.TryParse(PersianTextHelper.ToEnglishDigits(TxtDefaultLon.Text.Trim()), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) && lon is >= -180 and <= 180)
            _settings.DefaultMapLongitude = lon;
        if (double.TryParse(PersianTextHelper.ToEnglishDigits(TxtDefaultZoom.Text.Trim()), NumberStyles.Float, CultureInfo.InvariantCulture, out double zm))
            _settings.DefaultMapZoom = Math.Clamp(zm, 3.0, 20.0);

        StartupCadPathChanged = !string.Equals(_initialCadPath, _settings.DefaultStartupCadPath, StringComparison.OrdinalIgnoreCase);

        SettingsService.Save(_settings);
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
