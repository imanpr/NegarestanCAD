using System.IO;
using System.Windows;
using Microsoft.Win32;
using NegarestanCAD.Models;
using NegarestanCAD.Services;

namespace NegarestanCAD.Views;

public partial class PrintPreviewWindow : Window
{
    private readonly CadRenderModel? _cadModel;
    private readonly IReadOnlyList<SurveyPoint> _points;
    private readonly PolygonMetrics _metrics;
    private readonly PrintSettings _settings;
    private readonly bool _persianDigits;
    private readonly int _activeZone;
    private readonly bool _activeNorthern;
    private readonly double _fallbackLat;
    private readonly double _fallbackLon;
    private bool _isInitializing = true;

    public PrintPreviewWindow(
        CadRenderModel? cadModel,
        IReadOnlyList<SurveyPoint> points,
        PolygonMetrics metrics,
        PrintSettings settings,
        bool persianDigits,
        int activeZone = 41,
        bool activeNorthern = true,
        double fallbackLat = 25.135843248135032,
        double fallbackLon = 61.18867633933179)
    {
        InitializeComponent();

        _cadModel = cadModel;
        _points = points;
        _metrics = metrics;
        _settings = settings;
        _persianDigits = persianDigits;
        _activeZone = activeZone;
        _activeNorthern = activeNorthern;
        _fallbackLat = fallbackLat;
        _fallbackLon = fallbackLon;

        // اگر فایل CAD لود نشده باشد و هنوز حالت چاپ تغییر نکرده، پیش‌فرض روی نقشه ماهواره‌ای باشد
        if (_cadModel == null && _settings.MapMode == PrintMapMode.CadOnly)
            _settings.MapMode = PrintMapMode.SatelliteOnly;

        LoadSettingsToUi();
        _isInitializing = false;
        RefreshPreview();
    }

    private void LoadSettingsToUi()
    {
        CmbPaperSize.SelectedIndex = _settings.PaperSize == PaperSizeOption.A3 ? 1 : 0;
        CmbOrientation.SelectedIndex = _settings.Orientation == PaperOrientationOption.Portrait ? 1 : 0;

        CmbPrintMapMode.SelectedIndex = _settings.MapMode switch
        {
            PrintMapMode.BothSideBySide => 0,
            PrintMapMode.CadOnly => 1,
            PrintMapMode.SatelliteOnly => 2,
            _ => 0
        };

        CmbPrintSatProvider.SelectedIndex = _settings.SatelliteProvider switch
        {
            SatelliteMapProvider.GoogleHybrid => 0,
            SatelliteMapProvider.GoogleSatellite => 1,
            SatelliteMapProvider.EsriWorldImagery => 2,
            SatelliteMapProvider.OpenStreetMap => 3,
            _ => 0
        };

        TxtProjectTitle.Text = _settings.ProjectTitle;
        TxtOwnerName.Text = _settings.OwnerName;
        TxtParcelNumber.Text = _settings.ParcelNumber;
        TxtLocation.Text = _settings.LocationAddress;
        TxtEngineer.Text = _settings.EngineerOrCompany;
        TxtLicense.Text = _settings.EngineerLicenseNumber;
        TxtShamsiDate.Text = string.IsNullOrWhiteSpace(_settings.CustomDateShamsi)
            ? PersianTextHelper.GetTodayShamsi(_persianDigits)
            : _settings.CustomDateShamsi;
        TxtScaleOverride.Text = _settings.ScaleLabelOverride;
        TxtNotes.Text = _settings.Notes;
        TxtLogoFile.Text = string.IsNullOrWhiteSpace(_settings.LogoPath) ? "بدون لوگو" : Path.GetFileName(_settings.LogoPath);

        ChkIncludeDwg.IsChecked = _settings.IncludeDwgUnderlay;
        ChkGrayscaleDwg.IsChecked = _settings.GrayscaleDwgUnderlay;
        ChkShowDistances.IsChecked = _settings.ShowEdgeDistances;
        ChkShowCoordTable.IsChecked = _settings.ShowCoordinatesTable;
        ChkShowEdgeTable.IsChecked = _settings.ShowEdgesTable;
    }

    private void SaveUiToSettings()
    {
        _settings.PaperSize = CmbPaperSize.SelectedIndex == 1 ? PaperSizeOption.A3 : PaperSizeOption.A4;
        _settings.Orientation = CmbOrientation.SelectedIndex == 1 ? PaperOrientationOption.Portrait : PaperOrientationOption.Landscape;

        _settings.MapMode = CmbPrintMapMode.SelectedIndex switch
        {
            1 => PrintMapMode.CadOnly,
            2 => PrintMapMode.SatelliteOnly,
            _ => PrintMapMode.BothSideBySide
        };

        _settings.SatelliteProvider = CmbPrintSatProvider.SelectedIndex switch
        {
            1 => SatelliteMapProvider.GoogleSatellite,
            2 => SatelliteMapProvider.EsriWorldImagery,
            3 => SatelliteMapProvider.OpenStreetMap,
            _ => SatelliteMapProvider.GoogleHybrid
        };

        _settings.ProjectTitle = TxtProjectTitle.Text.Trim();
        _settings.OwnerName = TxtOwnerName.Text.Trim();
        _settings.ParcelNumber = TxtParcelNumber.Text.Trim();
        _settings.LocationAddress = TxtLocation.Text.Trim();
        _settings.EngineerOrCompany = TxtEngineer.Text.Trim();
        _settings.EngineerLicenseNumber = TxtLicense.Text.Trim();
        _settings.CustomDateShamsi = TxtShamsiDate.Text.Trim();
        _settings.ScaleLabelOverride = TxtScaleOverride.Text.Trim();
        _settings.Notes = TxtNotes.Text.Trim();

        _settings.IncludeDwgUnderlay = ChkIncludeDwg.IsChecked == true;
        _settings.GrayscaleDwgUnderlay = ChkGrayscaleDwg.IsChecked == true;
        _settings.ShowEdgeDistances = ChkShowDistances.IsChecked == true;
        _settings.ShowCoordinatesTable = ChkShowCoordTable.IsChecked == true;
        _settings.ShowEdgesTable = ChkShowEdgeTable.IsChecked == true;
    }

    private void AnySettingChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SaveUiToSettings();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        PreviewHost.Child = OfficialPrintService.BuildOfficialSheetVisual(
            _cadModel, _points, _metrics, _settings, _persianDigits,
            _activeZone, _activeNorthern, _fallbackLat, _fallbackLon);
    }

    private void BtnBrowseLogo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "انتخاب تصویر لوگو",
            Filter = "تصاویر (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dlg.ShowDialog() == true)
        {
            _settings.LogoPath = dlg.FileName;
            TxtLogoFile.Text = Path.GetFileName(dlg.FileName);
            RefreshPreview();
        }
    }

    private void BtnClearLogo_Click(object sender, RoutedEventArgs e)
    {
        _settings.LogoPath = string.Empty;
        TxtLogoFile.Text = "بدون لوگو";
        RefreshPreview();
    }

    private void BtnPrintNow_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        try
        {
            bool printed = OfficialPrintService.PrintSheet(
                _cadModel, _points, _metrics, _settings, _persianDigits,
                _activeZone, _activeNorthern, _fallbackLat, _fallbackLon);
            if (printed)
                MessageBox.Show("سند رسمی با موفقیت به چاپگر ارسال شد.", "چاپ موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در فرآیند چاپ:\n{ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportPng_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        var dlg = new SaveFileDialog
        {
            Title = "ذخیره تصویر رسمی 300 DPI",
            Filter = "تصویر PNG (*.png)|*.png",
            FileName = "OfficialSurveySheet.png"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            OfficialPrintService.ExportSheetToPng(
                dlg.FileName, _cadModel, _points, _metrics, _settings, _persianDigits,
                _activeZone, _activeNorthern, _fallbackLat, _fallbackLon);
            MessageBox.Show("تصویر باکیفیت 300 DPI با موفقیت ذخیره شد.", "ذخیره موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در ذخیره تصویر:\n{ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
