using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NegarestanCAD.Controls;
using NegarestanCAD.Models;
using NegarestanCAD.Services;

namespace NegarestanCAD.Views;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SurveyPoint> _points = new();
    private CadRenderModel? _cadModel;
    private PolygonMetrics _metrics = new();
    private PrintSettings _printSettings = new();
    private AppSettings _appSettings = new();
    private string? _currentProjectPath;

    private WorkspaceViewMode _currentViewMode = WorkspaceViewMode.SplitBoth;
    private bool _isSyncingViewports;
    private bool _isInitializingUi = true;

    // متغیرهای Drag & Drop برای تغییر ترتیب نقاط در لیست
    private Point _dragStartPoint;
    private SurveyPoint? _draggedPoint;

    public MainWindow()
    {
        InitializeComponent();

        // بارگذاری تنظیمات پایدار کاربر
        _appSettings = SettingsService.Load();

        // پر کردن لیست کامل 60 زون UTM
        for (int z = 1; z <= 60; z++)
            CmbUtmZone.Items.Add(z);
        CmbUtmZone.SelectedItem = Math.Clamp(_appSettings.DefaultUtmZone, 1, 60);
        CmbHemisphere.SelectedIndex = _appSettings.DefaultIsNorthern ? 0 : 1;

        LstPoints.ItemsSource = _points;

        // اتصال رویدادهای بوم CAD
        CadCanvas.CursorWorldMoved += CadCanvas_CursorWorldMoved;
        CadCanvas.PointClickedToAdd += CadCanvas_PointClickedToAdd;
        CadCanvas.PointDragged += CadCanvas_PointDragged;
        CadCanvas.MeasurementUpdated += msg => TxtStatusMeasure.Text = msg;
        CadCanvas.ViewportChanged += CadCanvas_ViewportChanged;

        // اتصال رویدادهای نقشه ماهواره‌ای
        SatelliteMap.CursorGeoMoved += SatelliteMap_CursorGeoMoved;
        SatelliteMap.PointClickedToAdd += SatelliteMap_PointClickedToAdd;
        SatelliteMap.PointDragged += SatelliteMap_PointDragged;
        SatelliteMap.MeasurementUpdated += msg => TxtStatusMeasure.Text = msg;
        SatelliteMap.ViewportChanged += SatelliteMap_ViewportChanged;

        ApplyPersistentSettingsToUi();
        SetToolMode(CanvasToolMode.Pan);
        _isInitializingUi = false;
        RecalculateAndRefresh();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // تنظیم موقعیت اولیه نقشه ماهواره‌ای روی مختصات پیش‌فرض (بریس)
        SatelliteMap.SetView(_appSettings.DefaultMapLatitude, _appSettings.DefaultMapLongitude, _appSettings.DefaultMapZoom, raiseEvent: false);

        // همگام‌سازی بوم خالی CAD با همان مختصات پیش‌فرض
        var initUtm = CoordinateConverter.LatLonToUtm(_appSettings.DefaultMapLatitude, _appSettings.DefaultMapLongitude, ActiveUtmZone);
        double initMpp = SatelliteTileService.MetersPerPixel(_appSettings.DefaultMapLatitude, _appSettings.DefaultMapZoom);
        CadCanvas.SetViewByCenterAndMetersPerPixel(initUtm.Easting, initUtm.Northing, initMpp, raiseEvent: false);

        // اگر فایل DWG پیش‌فرض در تنظیمات ثبت شده و موجود باشد، خودکار باز شود
        if (!string.IsNullOrWhiteSpace(_appSettings.DefaultStartupCadPath) && File.Exists(_appSettings.DefaultStartupCadPath))
        {
            await LoadCadFromPathAsync(_appSettings.DefaultStartupCadPath);
        }
    }

    private void ApplyPersistentSettingsToUi()
    {
        // اعمال تنظیمات پیش‌فرض سازمان و لوگو به تنظیمات چاپ رسمی
        _printSettings.EngineerOrCompany = string.IsNullOrWhiteSpace(_appSettings.DefaultOrganizationName)
            ? "شهرداری بریس"
            : _appSettings.DefaultOrganizationName;
        if (!string.IsNullOrWhiteSpace(_appSettings.DefaultLicenseNumber))
            _printSettings.EngineerLicenseNumber = _appSettings.DefaultLicenseNumber;
        if (!string.IsNullOrWhiteSpace(_appSettings.DefaultLogoPath) && File.Exists(_appSettings.DefaultLogoPath))
            _printSettings.LogoPath = _appSettings.DefaultLogoPath;
        _printSettings.SatelliteProvider = _appSettings.DefaultSatelliteProvider;

        // تم بوم CAD
        CadCanvas.IsDarkBackground = _appSettings.IsDarkCanvas;
        BtnToggleTheme.Content = _appSettings.IsDarkCanvas ? "☀️ بوم روشن" : "🌙 بوم تیره";

        // سرویس‌دهنده نقشه ماهواره‌ای
        SatelliteMap.Provider = _appSettings.DefaultSatelliteProvider;
        CmbSatProviderToolbar.SelectedIndex = _appSettings.DefaultSatelliteProvider switch
        {
            SatelliteMapProvider.GoogleHybrid => 0,
            SatelliteMapProvider.GoogleSatellite => 1,
            SatelliteMapProvider.EsriWorldImagery => 2,
            SatelliteMapProvider.OpenStreetMap => 3,
            _ => 0
        };

        ChkSyncViewports.IsChecked = _appSettings.SyncCadAndSatellite;
        ChkEnableSnap.IsChecked = _appSettings.EnableSnap;
        ChkShowDistances.IsChecked = _appSettings.ShowEdgeDistances;
        ChkShowAngles.IsChecked = _appSettings.ShowInteriorAngles;
        ChkUseGroundArea.IsChecked = _appSettings.UseGroundMeasurements;
        ChkFixShx.IsChecked = _appSettings.FixLegacyPersianShx;
        ChkPersianDigits.IsChecked = _appSettings.UsePersianDigits;

        SetViewMode(_appSettings.DefaultViewMode);
    }

    private int ActiveUtmZone => CmbUtmZone.SelectedItem is int z ? z : 41;
    private bool ActiveIsNorthern => CmbHemisphere.SelectedIndex != 1;

    #region کنترل سه حالته نمایش و همگام‌سازی زنده دوطرفه CAD ↔ ماهواره

    private void BtnViewCadOnly_Click(object sender, RoutedEventArgs e) => SetViewMode(WorkspaceViewMode.CadOnly);
    private void BtnViewSatOnly_Click(object sender, RoutedEventArgs e) => SetViewMode(WorkspaceViewMode.SatelliteOnly);
    private void BtnViewBoth_Click(object sender, RoutedEventArgs e) => SetViewMode(WorkspaceViewMode.SplitBoth);

    private void SetViewMode(WorkspaceViewMode mode)
    {
        _currentViewMode = mode;

        var activeBrush = new SolidColorBrush(Color.FromRgb(14, 165, 233));
        var idleBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));

        BtnViewCadOnly.Background = mode == WorkspaceViewMode.CadOnly ? activeBrush : idleBrush;
        BtnViewSatOnly.Background = mode == WorkspaceViewMode.SatelliteOnly ? activeBrush : idleBrush;
        BtnViewBoth.Background = mode == WorkspaceViewMode.SplitBoth ? activeBrush : idleBrush;

        switch (mode)
        {
            case WorkspaceViewMode.CadOnly:
                ColCad.Width = new GridLength(1, GridUnitType.Star);
                ColMapSplitter.Width = new GridLength(0);
                ColSat.Width = new GridLength(0);
                CadContainer.Visibility = Visibility.Visible;
                MapGridSplitter.Visibility = Visibility.Collapsed;
                SatContainer.Visibility = Visibility.Collapsed;
                break;

            case WorkspaceViewMode.SatelliteOnly:
                ColCad.Width = new GridLength(0);
                ColMapSplitter.Width = new GridLength(0);
                ColSat.Width = new GridLength(1, GridUnitType.Star);
                CadContainer.Visibility = Visibility.Collapsed;
                MapGridSplitter.Visibility = Visibility.Collapsed;
                SatContainer.Visibility = Visibility.Visible;
                break;

            case WorkspaceViewMode.SplitBoth:
                ColCad.Width = new GridLength(1, GridUnitType.Star);
                ColMapSplitter.Width = new GridLength(5);
                ColSat.Width = new GridLength(1, GridUnitType.Star);
                CadContainer.Visibility = Visibility.Visible;
                MapGridSplitter.Visibility = Visibility.Visible;
                SatContainer.Visibility = Visibility.Visible;
                break;
        }

        CadCanvas.InvalidateCadCache();
        SatelliteMap.InvalidateVisual();
    }

    private void CmbSatProviderToolbar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi) return;
        var provider = CmbSatProviderToolbar.SelectedIndex switch
        {
            1 => SatelliteMapProvider.GoogleSatellite,
            2 => SatelliteMapProvider.EsriWorldImagery,
            3 => SatelliteMapProvider.OpenStreetMap,
            _ => SatelliteMapProvider.GoogleHybrid
        };
        SatelliteMap.Provider = provider;
        _printSettings.SatelliteProvider = provider;
        _appSettings.DefaultSatelliteProvider = provider;
        SatelliteMap.InvalidateVisual();
    }

    private void ChkSyncViewports_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingUi) return;
        _appSettings.SyncCadAndSatellite = ChkSyncViewports.IsChecked == true;
    }

    private void CadCanvas_ViewportChanged(double utmX, double utmY, double metersPerPixel)
    {
        if (_isSyncingViewports || !_appSettings.SyncCadAndSatellite) return;
        if (double.IsNaN(utmX) || double.IsNaN(utmY) || double.IsNaN(metersPerPixel) || metersPerPixel <= 0) return;

        try
        {
            _isSyncingViewports = true;
            var geo = CoordinateConverter.UtmToLatLon(utmX, utmY, ActiveUtmZone, ActiveIsNorthern);
            if (geo.Latitude is >= -85 and <= 85 && geo.Longitude is >= -180 and <= 180)
            {
                SatelliteMap.SetViewByCenterAndMetersPerPixel(geo.Latitude, geo.Longitude, metersPerPixel, raiseEvent: false);
            }
        }
        catch { }
        finally
        {
            _isSyncingViewports = false;
        }
    }

    private void SatelliteMap_ViewportChanged(double lat, double lon, double metersPerPixel)
    {
        if (_isSyncingViewports || !_appSettings.SyncCadAndSatellite) return;
        if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsNaN(metersPerPixel) || metersPerPixel <= 0) return;

        try
        {
            _isSyncingViewports = true;
            var utm = CoordinateConverter.LatLonToUtm(lat, lon, ActiveUtmZone);
            CadCanvas.SetViewByCenterAndMetersPerPixel(utm.Easting, utm.Northing, metersPerPixel, raiseEvent: false);
        }
        catch { }
        finally
        {
            _isSyncingViewports = false;
        }
    }

    #endregion

    #region مدیریت فایل‌های DWG / DXF و پروژه (بارگذاری غیرمسدودکننده)

    private async void BtnOpenCad_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "انتخاب فایل نقشه اتوکد (DWG / DXF)",
            Filter = "فایل‌های نقشه اتوکد (*.dwg;*.dxf)|*.dwg;*.dxf|فایل DWG (*.dwg)|*.dwg|فایل DXF (*.dxf)|*.dxf"
        };

        if (dlg.ShowDialog() == true)
            await LoadCadFromPathAsync(dlg.FileName);
    }

    private async Task LoadCadFromPathAsync(string path)
    {
        try
        {
            OverlayLoading.Visibility = Visibility.Visible;
            TxtLoadingMessage.Text = $"در حال خواندن و پردازش {Path.GetFileName(path)} ...";
            Mouse.OverrideCursor = Cursors.Wait;

            bool fixShx = _appSettings.FixLegacyPersianShx;
            var loadedModel = await Task.Run(() => CadFileService.LoadCadFile(path, fixShx));

            _cadModel = loadedModel;
            CadCanvas.SetCadModel(_cadModel);

            TxtLoadedCadFile.Text = $"{_cadModel.FileName} ({_cadModel.TotalEntityCount:N0} المان)";
            RefreshLayerList();
            RecalculateAndRefresh();

            // همگام‌سازی فوری نقشه ماهواره‌ای با محدوده اولیه نقشه CAD بازشده
            var (cx, cy, mpp) = CadCanvas.GetViewportCenterAndMetersPerPixel();
            CadCanvas_ViewportChanged(cx, cy, mpp);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در خواندن فایل نقشه:\n{ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            OverlayLoading.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = null;
        }
    }

    private void BtnSaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "ذخیره فایل پروژه نگارستان کَد",
            Filter = "فایل پروژه نگارستان کَد (*.cadproj)|*.cadproj",
            FileName = string.IsNullOrWhiteSpace(_currentProjectPath) ? "SurveyProject.cadproj" : Path.GetFileName(_currentProjectPath)
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var doc = new ProjectDocument
            {
                ProjectName = _printSettings.ProjectTitle,
                LinkedCadFilePath = _cadModel?.FilePath,
                UtmZone = ActiveUtmZone,
                IsNorthernHemisphere = ActiveIsNorthern,
                Points = _points.ToList(),
                PrintSettings = _printSettings,
                AppSettings = _appSettings
            };
            ProjectIoService.SaveProject(dlg.FileName, doc);
            _currentProjectPath = dlg.FileName;
            MessageBox.Show("پروژه با موفقیت ذخیره شد.", "ذخیره پروژه", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در ذخیره پروژه:\n{ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnLoadProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "باز کردن فایل پروژه نگارستان کَد",
            Filter = "فایل پروژه نگارستان کَد (*.cadproj)|*.cadproj"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var doc = ProjectIoService.LoadProject(dlg.FileName);
            _currentProjectPath = dlg.FileName;
            _printSettings = doc.PrintSettings ?? new PrintSettings();

            // اگر لوگوی پروژه خالی بود، از لوگوی پیش‌فرض تنظیمات استفاده شود
            if (string.IsNullOrWhiteSpace(_printSettings.LogoPath) && !string.IsNullOrWhiteSpace(_appSettings.DefaultLogoPath))
                _printSettings.LogoPath = _appSettings.DefaultLogoPath;

            CmbUtmZone.SelectedItem = Math.Clamp(doc.UtmZone, 1, 60);
            CmbHemisphere.SelectedIndex = doc.IsNorthernHemisphere ? 0 : 1;

            _points.Clear();
            foreach (var p in doc.Points) _points.Add(p);

            if (!string.IsNullOrWhiteSpace(doc.LinkedCadFilePath) && File.Exists(doc.LinkedCadFilePath))
                await LoadCadFromPathAsync(doc.LinkedCadFilePath);

            RecalculateAndRefresh();
            ZoomBothToPoints();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در باز کردن پروژه:\n{ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region ورود، تبدیل دوطرفه و رفتن به مختصات

    private void BtnConvertUtmToLatLon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDouble(TxtUtmX.Text, out double x) || !TryParseDouble(TxtUtmY.Text, out double y))
        {
            MessageBox.Show("لطفاً مقادیر عددی معتبر برای X و Y (UTM) وارد کنید.", "ورودی نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var geo = CoordinateConverter.UtmToLatLon(x, y, ActiveUtmZone, ActiveIsNorthern);
        TxtLat.Text = geo.Latitude.ToString("F7", CultureInfo.InvariantCulture);
        TxtLon.Text = geo.Longitude.ToString("F7", CultureInfo.InvariantCulture);
        TxtDmsPreview.Text = $"DMS: {CoordinateConverter.ToDmsString(geo.Latitude, true)}  |  {CoordinateConverter.ToDmsString(geo.Longitude, false)}";
    }

    private void BtnConvertLatLonToUtm_Click(object sender, RoutedEventArgs e)
    {
        if (!CoordinateConverter.TryParseGeoAngle(TxtLat.Text, out double lat) ||
            !CoordinateConverter.TryParseGeoAngle(TxtLon.Text, out double lon))
        {
            MessageBox.Show("لطفاً عرض و طول جغرافیایی معتبر (اعشاری یا DMS) وارد کنید.", "ورودی نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var utm = CoordinateConverter.LatLonToUtm(lat, lon, ActiveUtmZone);
        TxtUtmX.Text = utm.Easting.ToString("F3", CultureInfo.InvariantCulture);
        TxtUtmY.Text = utm.Northing.ToString("F3", CultureInfo.InvariantCulture);
        TxtDmsPreview.Text = $"DMS: {CoordinateConverter.ToDmsString(lat, true)}  |  {CoordinateConverter.ToDmsString(lon, false)}";
    }

    private void BtnGoToCoord_Click(object sender, RoutedEventArgs e)
    {
        double easting, northing, lat, lon;
        if (TryParseDouble(TxtUtmX.Text, out easting) && TryParseDouble(TxtUtmY.Text, out northing))
        {
            var geo = CoordinateConverter.UtmToLatLon(easting, northing, ActiveUtmZone, ActiveIsNorthern);
            lat = geo.Latitude; lon = geo.Longitude;
        }
        else if (CoordinateConverter.TryParseGeoAngle(TxtLat.Text, out lat) && CoordinateConverter.TryParseGeoAngle(TxtLon.Text, out lon))
        {
            var utm = CoordinateConverter.LatLonToUtm(lat, lon, ActiveUtmZone);
            easting = utm.Easting; northing = utm.Northing;
        }
        else
        {
            MessageBox.Show("ابتدا مختصات UTM یا Lat/Lon را وارد کنید.", "مختصات خالی", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SatelliteMap.SetView(lat, lon, 18.0, raiseEvent: false);
        double mpp = SatelliteTileService.MetersPerPixel(lat, 18.0);
        CadCanvas.SetViewByCenterAndMetersPerPixel(easting, northing, mpp, raiseEvent: false);
    }

    private void BtnAddPoint_Click(object sender, RoutedEventArgs e)
    {
        double easting = 0, northing = 0, lat = 0, lon = 0;

        bool hasUtm = TryParseDouble(TxtUtmX.Text, out easting) && TryParseDouble(TxtUtmY.Text, out northing);
        bool hasGeo = CoordinateConverter.TryParseGeoAngle(TxtLat.Text, out lat) && CoordinateConverter.TryParseGeoAngle(TxtLon.Text, out lon);

        if (hasUtm)
        {
            var geo = CoordinateConverter.UtmToLatLon(easting, northing, ActiveUtmZone, ActiveIsNorthern);
            lat = geo.Latitude;
            lon = geo.Longitude;
        }
        else if (hasGeo)
        {
            var utm = CoordinateConverter.LatLonToUtm(lat, lon, ActiveUtmZone);
            easting = utm.Easting;
            northing = utm.Northing;
        }
        else
        {
            MessageBox.Show("لطفاً مختصات UTM یا طول و عرض جغرافیایی را وارد کنید.", "ورودی ناقص", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddSurveyPoint(easting, northing, lat, lon, TxtPointDesc.Text.Trim());
        TxtUtmX.Clear();
        TxtUtmY.Clear();
        TxtLat.Clear();
        TxtLon.Clear();
        TxtPointDesc.Clear();
        TxtDmsPreview.Text = string.Empty;
    }

    private void CadCanvas_PointClickedToAdd(double wx, double wy)
    {
        var geo = CoordinateConverter.UtmToLatLon(wx, wy, ActiveUtmZone, ActiveIsNorthern);
        AddSurveyPoint(wx, wy, geo.Latitude, geo.Longitude, "ثبت از روی نقشه CAD");
    }

    private void SatelliteMap_PointClickedToAdd(double lat, double lon)
    {
        var utm = CoordinateConverter.LatLonToUtm(lat, lon, ActiveUtmZone);
        AddSurveyPoint(utm.Easting, utm.Northing, lat, lon, "ثبت از روی نقشه ماهواره‌ای");
    }

    private void CadCanvas_PointDragged(int index, double wx, double wy)
    {
        if (index < 0 || index >= _points.Count) return;
        var pt = _points[index];
        pt.Easting = wx;
        pt.Northing = wy;
        var geo = CoordinateConverter.UtmToLatLon(wx, wy, pt.UtmZone, pt.IsNorthernHemisphere);
        pt.Latitude = geo.Latitude;
        pt.Longitude = geo.Longitude;
        SyncSelectedPointEditors(index, pt);
    }

    private void SatelliteMap_PointDragged(int index, double lat, double lon)
    {
        if (index < 0 || index >= _points.Count) return;
        var pt = _points[index];
        pt.Latitude = lat;
        pt.Longitude = lon;
        var utm = CoordinateConverter.LatLonToUtm(lat, lon, pt.UtmZone);
        pt.Easting = utm.Easting;
        pt.Northing = utm.Northing;
        SyncSelectedPointEditors(index, pt);
    }

    private void SyncSelectedPointEditors(int index, SurveyPoint pt)
    {
        LstPoints.SelectedIndex = index;
        TxtUtmX.Text = pt.Easting.ToString("F3", CultureInfo.InvariantCulture);
        TxtUtmY.Text = pt.Northing.ToString("F3", CultureInfo.InvariantCulture);
        TxtLat.Text = pt.Latitude.ToString("F7", CultureInfo.InvariantCulture);
        TxtLon.Text = pt.Longitude.ToString("F7", CultureInfo.InvariantCulture);
        TxtDmsPreview.Text = $"DMS: {CoordinateConverter.ToDmsString(pt.Latitude, true)}  |  {CoordinateConverter.ToDmsString(pt.Longitude, false)}";
        RecalculateAndRefresh();
    }

    private void AddSurveyPoint(double easting, double northing, double lat, double lon, string desc)
    {
        _points.Add(new SurveyPoint
        {
            Easting = easting,
            Northing = northing,
            Latitude = lat,
            Longitude = lon,
            UtmZone = ActiveUtmZone,
            IsNorthernHemisphere = ActiveIsNorthern,
            Description = desc
        });

        GeometryCalculator.ReindexAndRelabel(_points);
        RecalculateAndRefresh();
    }

    #endregion

    #region مدیریت لیست نقاط (Drag & Drop، جابجایی، مرتب‌سازی ساعتگرد و حذف)

    private void LstPoints_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        var item = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        _draggedPoint = item?.DataContext as SurveyPoint;
    }

    private void LstPoints_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedPoint == null) return;

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(LstPoints, _draggedPoint, DragDropEffects.Move);
            _draggedPoint = null;
        }
    }

    private void LstPoints_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(SurveyPoint))) return;
        var droppedData = (SurveyPoint)e.Data.GetData(typeof(SurveyPoint));
        var targetItem = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        var targetData = targetItem?.DataContext as SurveyPoint;

        int oldIndex = _points.IndexOf(droppedData);
        int newIndex = targetData != null ? _points.IndexOf(targetData) : _points.Count - 1;

        if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
        {
            _points.Move(oldIndex, newIndex);
            GeometryCalculator.ReindexAndRelabel(_points);
            LstPoints.SelectedIndex = newIndex;
            RecalculateAndRefresh();
        }
    }

    private void LstPoints_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CadCanvas.SelectedPointIndex = LstPoints.SelectedIndex;
        SatelliteMap.SelectedPointIndex = LstPoints.SelectedIndex;
        CadCanvas.InvalidateVisual();
        SatelliteMap.InvalidateVisual();

        if (LstPoints.SelectedItem is SurveyPoint p)
        {
            TxtUtmX.Text = p.Easting.ToString("F3", CultureInfo.InvariantCulture);
            TxtUtmY.Text = p.Northing.ToString("F3", CultureInfo.InvariantCulture);
            TxtLat.Text = p.Latitude.ToString("F7", CultureInfo.InvariantCulture);
            TxtLon.Text = p.Longitude.ToString("F7", CultureInfo.InvariantCulture);
            TxtDmsPreview.Text = $"DMS: {CoordinateConverter.ToDmsString(p.Latitude, true)}  |  {CoordinateConverter.ToDmsString(p.Longitude, false)}";
        }
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstPoints.SelectedIndex;
        if (idx <= 0) return;
        _points.Move(idx, idx - 1);
        GeometryCalculator.ReindexAndRelabel(_points);
        LstPoints.SelectedIndex = idx - 1;
        RecalculateAndRefresh();
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstPoints.SelectedIndex;
        if (idx < 0 || idx >= _points.Count - 1) return;
        _points.Move(idx, idx + 1);
        GeometryCalculator.ReindexAndRelabel(_points);
        LstPoints.SelectedIndex = idx + 1;
        RecalculateAndRefresh();
    }

    private void BtnSortClockwise_Click(object sender, RoutedEventArgs e)
    {
        if (_points.Count < 3) return;
        var sorted = GeometryCalculator.SortPointsRadially(_points, clockwise: true);
        _points.Clear();
        foreach (var p in sorted) _points.Add(p);
        GeometryCalculator.ReindexAndRelabel(_points);
        RecalculateAndRefresh();
    }

    private void BtnDeletePoint_Click(object sender, RoutedEventArgs e)
    {
        int idx = LstPoints.SelectedIndex;
        if (idx < 0 || idx >= _points.Count) return;
        _points.RemoveAt(idx);
        GeometryCalculator.ReindexAndRelabel(_points);
        RecalculateAndRefresh();
    }

    private void BtnClearPoints_Click(object sender, RoutedEventArgs e)
    {
        if (_points.Count == 0) return;
        if (MessageBox.Show("آیا از پاک کردن تمام نقاط محدوده مطمئن هستید؟", "تأیید", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _points.Clear();
            RecalculateAndRefresh();
        }
    }

    #endregion

    #region ایمپورت و اکسپورت (Excel / CSV / DXF / KML-KMZ)

    private void BtnImportPoints_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "ایمپورت نقاط از Excel یا CSV/TXT",
            Filter = "فایل‌های مختصات (*.xlsx;*.csv;*.txt)|*.xlsx;*.csv;*.txt|اکسل (*.xlsx)|*.xlsx|CSV/TXT (*.csv;*.txt)|*.csv;*.txt"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var imported = ext == ".xlsx"
                ? ProjectIoService.ImportExcel(dlg.FileName, ActiveUtmZone, ActiveIsNorthern)
                : ProjectIoService.ImportCsvOrTxt(dlg.FileName, ActiveUtmZone, ActiveIsNorthern);

            if (imported.Count == 0)
            {
                MessageBox.Show("هیچ نقطه معتبری در فایل یافت نشد.", "هشدار", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _points.Clear();
            foreach (var p in imported) _points.Add(p);
            GeometryCalculator.ReindexAndRelabel(_points);
            RecalculateAndRefresh();
            ZoomBothToPoints();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطا در ایمپورت نقاط:\n{ex.Message}", "خطا", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePointsExist()) return;
        var dlg = new SaveFileDialog { Title = "خروجی اکسل", Filter = "فایل اکسل (*.xlsx)|*.xlsx", FileName = "SurveyPoints.xlsx" };
        if (dlg.ShowDialog() == true)
        {
            ProjectIoService.ExportExcel(dlg.FileName, _points, _metrics, _printSettings);
            MessageBox.Show("فایل اکسل با موفقیت ذخیره شد.", "موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePointsExist()) return;
        var dlg = new SaveFileDialog { Title = "خروجی CSV", Filter = "فایل CSV (*.csv)|*.csv|فایل متنی (*.txt)|*.txt", FileName = "SurveyPoints.csv" };
        if (dlg.ShowDialog() == true)
        {
            ProjectIoService.ExportCsv(dlg.FileName, _points);
            MessageBox.Show("فایل CSV با موفقیت ذخیره شد.", "موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnExportDxf_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePointsExist()) return;
        var dlg = new SaveFileDialog { Title = "خروجی اتوکد DXF", Filter = "فایل اتوکد (*.dxf)|*.dxf", FileName = "SurveyBoundary.dxf" };
        if (dlg.ShowDialog() == true)
        {
            CadFileService.ExportPointsToDxf(dlg.FileName, _points, _metrics, _appSettings.UseGroundMeasurements);
            MessageBox.Show("فایل DXF (شامل محدوده، نقاط، برچسب‌ها و متراژ) با موفقیت ساخته شد.", "موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnExportKml_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePointsExist()) return;
        var dlg = new SaveFileDialog { Title = "خروجی گوگل‌ارث KML/KMZ", Filter = "Google Earth KMZ (*.kmz)|*.kmz|Google Earth KML (*.kml)|*.kml", FileName = "SurveyParcel.kmz" };
        if (dlg.ShowDialog() == true)
        {
            ProjectIoService.ExportKmlOrKmz(dlg.FileName, _points, _metrics, _printSettings.ProjectTitle);
            MessageBox.Show("فایل Google Earth با موفقیت ذخیره شد.", "موفق", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool EnsurePointsExist()
    {
        if (_points.Count > 0) return true;
        MessageBox.Show("ابتدا حداقل یک نقطه وارد کنید.", "نقطه‌ای وجود ندارد", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    #endregion

    #region پرینت رسمی، تنظیمات، درباره ما و ابزارهای بوم

    private void BtnOfficialPrint_Click(object sender, RoutedEventArgs e)
    {
        _printSettings.UseGroundArea = _appSettings.UseGroundMeasurements;
        _printSettings.SatelliteProvider = SatelliteMap.Provider;

        if (string.IsNullOrWhiteSpace(_printSettings.EngineerOrCompany) && !string.IsNullOrWhiteSpace(_appSettings.DefaultOrganizationName))
            _printSettings.EngineerOrCompany = _appSettings.DefaultOrganizationName;
        if (string.IsNullOrWhiteSpace(_printSettings.LogoPath) && !string.IsNullOrWhiteSpace(_appSettings.DefaultLogoPath))
            _printSettings.LogoPath = _appSettings.DefaultLogoPath;

        // اگر فایل CAD باز نشده باشد، به صورت پیش‌فرض چاپ را روی نقشه ماهواره‌ای بگذار
        if (_cadModel == null && _printSettings.MapMode != PrintMapMode.SatelliteOnly)
            _printSettings.MapMode = PrintMapMode.SatelliteOnly;

        var win = new PrintPreviewWindow(
            _cadModel, _points.ToList(), _metrics, _printSettings, _appSettings.UsePersianDigits,
            ActiveUtmZone, ActiveIsNorthern, SatelliteMap.CenterLat, SatelliteMap.CenterLon)
        {
            Owner = this
        };
        win.ShowDialog();
    }

    private async void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_appSettings) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _isInitializingUi = true;
            ApplyPersistentSettingsToUi();
            _isInitializingUi = false;

            CadCanvas.InvalidateCadCache();
            SatelliteMap.InvalidateVisual();

            if (win.StartupCadPathChanged && !string.IsNullOrWhiteSpace(_appSettings.DefaultStartupCadPath) && File.Exists(_appSettings.DefaultStartupCadPath))
            {
                await LoadCadFromPathAsync(_appSettings.DefaultStartupCadPath);
            }
        }
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "درباره برنامه — نگارستان کَد",
            Width = 450,
            Height = 365,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = App.PersianFontFamily,
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252))
        };

        var card = new Border
        {
            Margin = new Thickness(18),
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        stack.Children.Add(new TextBlock
        {
            Text = "نگارستان کَد (NegarestanCAD)",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        stack.Children.Add(new TextBlock
        {
            Text = "سامانه نمایش DWG، نقشه ماهواره‌ای و نقشه‌برداری UTM — نسخه 1.2.0",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 14)
        });

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "توسعه یافته توسط شهرداری بریس",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 5)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "ایمان پرهیزکار",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "سال 1405",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10)
        });

        static System.Windows.Documents.Hyperlink MakeLink(string display, string url)
        {
            var hl = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(display))
            {
                NavigateUri = new Uri(url),
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248))
            };
            hl.RequestNavigate += (_, args) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
                args.Handled = true;
            };
            return hl;
        }

        var linksBlock = new TextBlock
        {
            FontSize = 12,
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        linksBlock.Inlines.Add(MakeLink("www.beris.ir", "https://www.beris.ir"));
        linksBlock.Inlines.Add(new System.Windows.Documents.Run("   |   ") { Foreground = Brushes.Gray });
        linksBlock.Inlines.Add(MakeLink("imanic.ir", "https://imanic.ir"));
        stack.Children.Add(linksBlock);

        var btnClose = new Button
        {
            Content = "بستن",
            Width = 100,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        btnClose.Click += (_, _) => win.Close();
        stack.Children.Add(btnClose);

        card.Child = stack;
        win.Content = card;
        win.ShowDialog();
    }

    private void BtnToolPan_Click(object sender, RoutedEventArgs e) => SetToolMode(CanvasToolMode.Pan);
    private void BtnToolAddPoint_Click(object sender, RoutedEventArgs e) => SetToolMode(CanvasToolMode.AddPoint);
    private void BtnToolRuler_Click(object sender, RoutedEventArgs e) => SetToolMode(CanvasToolMode.Ruler);
    private void BtnToolAngle_Click(object sender, RoutedEventArgs e) => SetToolMode(CanvasToolMode.AngleMeasure);

    private void SetToolMode(CanvasToolMode mode)
    {
        CadCanvas.ToolMode = mode;
        SatelliteMap.ToolMode = mode;
        CadCanvas.ClearMeasurements();
        SatelliteMap.ClearMeasurements();

        var activeBrush = new SolidColorBrush(Color.FromRgb(14, 165, 233));
        var idleBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));

        BtnToolPan.Background = mode == CanvasToolMode.Pan ? activeBrush : idleBrush;
        BtnToolAddPoint.Background = mode == CanvasToolMode.AddPoint ? activeBrush : idleBrush;
        BtnToolRuler.Background = mode == CanvasToolMode.Ruler ? activeBrush : idleBrush;
        BtnToolAngle.Background = mode == CanvasToolMode.AngleMeasure ? activeBrush : idleBrush;

        TxtActiveToolHint.Text = mode switch
        {
            CanvasToolMode.Pan => "ابزار فعال: جابجایی نقشه (با اسکرول زوم کنید و نقاط را با درگ جابجا کنید)",
            CanvasToolMode.AddPoint => "ابزار فعال: ثبت نقطه با کلیک (روی نقشه CAD یا ماهواره کلیک کنید)",
            CanvasToolMode.Ruler => "ابزار فعال: خط‌کش آزاد (دو نقطه روی نقشه CAD یا ماهواره کلیک کنید)",
            CanvasToolMode.AngleMeasure => "ابزار فعال: زاویه‌سنج (سه نقطه: ضلع اول، رأس، ضلع دوم)",
            _ => string.Empty
        };
    }

    private void BtnZoomExtents_Click(object sender, RoutedEventArgs e)
    {
        if (_cadModel != null && _cadModel.HasGeometry)
        {
            CadCanvas.ZoomToExtents();
        }
        else if (_points.Count > 0)
        {
            ZoomBothToPoints();
        }
        else
        {
            SatelliteMap.SetView(_appSettings.DefaultMapLatitude, _appSettings.DefaultMapLongitude, _appSettings.DefaultMapZoom, raiseEvent: true);
        }
    }

    private void BtnZoomPoints_Click(object sender, RoutedEventArgs e) => ZoomBothToPoints();

    private void ZoomBothToPoints()
    {
        if (_points.Count == 0)
        {
            BtnZoomExtents_Click(this, new RoutedEventArgs());
            return;
        }

        if (_currentViewMode == WorkspaceViewMode.SatelliteOnly)
            SatelliteMap.ZoomToSurveyPoints(raiseEvent: true);
        else
            CadCanvas.ZoomToSurveyPoints();
    }

    private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.IsDarkCanvas = !_appSettings.IsDarkCanvas;
        CadCanvas.IsDarkBackground = _appSettings.IsDarkCanvas;
        BtnToggleTheme.Content = _appSettings.IsDarkCanvas ? "☀️ بوم روشن" : "🌙 بوم تیره";
        CadCanvas.InvalidateCadCache();
        SettingsService.Save(_appSettings);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.O) { BtnOpenCad_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.S) { BtnSaveProject_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.P) { BtnOfficialPrint_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }
        else if (e.Key == Key.Escape)
        {
            if (QuickCoordBar.Visibility == Visibility.Visible)
            {
                QuickCoordBar.Visibility = Visibility.Collapsed;
                CadCanvas.Focus();
                e.Handled = true;
                return;
            }
            SetToolMode(CanvasToolMode.Pan);
        }
        else if ((e.Key == Key.Add || e.Key == Key.OemPlus) && Keyboard.FocusedElement is not TextBox)
        {
            OpenQuickCoordEntry();
            e.Handled = true;
        }
    }


    private void OpenQuickCoordEntry()
    {
        string nextLabel = GeometryCalculator.IndexToAlphabetLabel(_points.Count);
        TxtQuickPointLabel.Text = $"ورود سریع نقطه {nextLabel}:";
        TxtQuickCoord.Clear();
        QuickCoordBar.Visibility = Visibility.Visible;
        TxtQuickCoord.Focus();
    }

    private void TxtQuickCoord_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        string raw = PersianTextHelper.ToEnglishDigits(TxtQuickCoord.Text.Trim());
        var parts = raw.Split(new[] { ',', '،', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v1) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v2))
        {
            MessageBox.Show("فرمت صحیح را وارد کنید؛ مثال:\n316715.368, 2781695.808", "ورودی نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtQuickCoord.SelectAll();
            return;
        }

        // تشخیص هوشمند: اگر در بازه طول/عرض جغرافیایی باشد به UTM تبدیل می‌شود، در غیر این صورت مستقیم UTM است
        if (Math.Abs(v1) <= 90.0 && Math.Abs(v2) <= 180.0)
        {
            var utm = CoordinateConverter.LatLonToUtm(v1, v2, ActiveUtmZone);
            AddSurveyPoint(utm.Easting, utm.Northing, v1, v2, "ورود سریع کیبورد");
        }
        else
        {
            var geo = CoordinateConverter.UtmToLatLon(v1, v2, ActiveUtmZone, ActiveIsNorthern);
            AddSurveyPoint(v1, v2, geo.Latitude, geo.Longitude, "ورود سریع کیبورد");
        }

        QuickCoordBar.Visibility = Visibility.Collapsed;
        CadCanvas.Focus();
    }


    private void CadCanvas_CursorWorldMoved(double wx, double wy, SnapCandidate? snap)
    {
        bool fa = _appSettings.UsePersianDigits;
        TxtStatusCoordsUtm.Text = $"UTM ({ActiveUtmZone}{(ActiveIsNorthern ? "N" : "S")}):  X = {PersianTextHelper.FormatPlainNumber(wx, 3, fa)}   |   Y = {PersianTextHelper.FormatPlainNumber(wy, 3, fa)}";

        try
        {
            var geo = CoordinateConverter.UtmToLatLon(wx, wy, ActiveUtmZone, ActiveIsNorthern);
            TxtStatusCoordsGeo.Text = $"Lat: {PersianTextHelper.FormatPlainNumber(geo.Latitude, 6, fa)}  |  Lon: {PersianTextHelper.FormatPlainNumber(geo.Longitude, 6, fa)}";
        }
        catch { }

        TxtStatusSnap.Text = snap.HasValue
            ? $"[SNAP: {snap.Value.Kind} | لایه: {snap.Value.LayerName}]"
            : string.Empty;
    }

    private void SatelliteMap_CursorGeoMoved(double lat, double lon)
    {
        bool fa = _appSettings.UsePersianDigits;
        TxtStatusCoordsGeo.Text = $"Lat: {PersianTextHelper.FormatPlainNumber(lat, 6, fa)}  |  Lon: {PersianTextHelper.FormatPlainNumber(lon, 6, fa)}";
        try
        {
            var utm = CoordinateConverter.LatLonToUtm(lat, lon, ActiveUtmZone);
            TxtStatusCoordsUtm.Text = $"UTM ({utm.Zone}{(utm.IsNorthern ? "N" : "S")}):  X = {PersianTextHelper.FormatPlainNumber(utm.Easting, 3, fa)}   |   Y = {PersianTextHelper.FormatPlainNumber(utm.Northing, 3, fa)}";
        }
        catch { }
        TxtStatusSnap.Text = "[نقشه ماهواره‌ای]";
    }

    private void RefreshLayerList()
    {
        if (_cadModel == null)
        {
            LstLayers.ItemsSource = null;
            return;
        }

        string q = TxtLayerSearch.Text?.Trim() ?? string.Empty;
        LstLayers.ItemsSource = string.IsNullOrEmpty(q)
            ? _cadModel.Layers
            : _cadModel.Layers.Where(l => l.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void TxtLayerSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshLayerList();
    private void ChkLayerVisible_Click(object sender, RoutedEventArgs e) => CadCanvas.RefreshVisibleLayers();

    private void BtnAllLayersOn_Click(object sender, RoutedEventArgs e)
    {
        if (_cadModel == null) return;
        foreach (var l in _cadModel.Layers) l.IsVisible = true;
        CadCanvas.RefreshVisibleLayers();
    }

    private void BtnAllLayersOff_Click(object sender, RoutedEventArgs e)
    {
        if (_cadModel == null) return;
        foreach (var l in _cadModel.Layers) l.IsVisible = false;
        CadCanvas.RefreshVisibleLayers();
    }

    private void CmbUtmZone_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi) return;
        SatelliteMap.ActiveUtmZone = ActiveUtmZone;
        SatelliteMap.ActiveIsNorthern = ActiveIsNorthern;

        foreach (var p in _points)
        {
            p.UtmZone = ActiveUtmZone;
            p.IsNorthernHemisphere = ActiveIsNorthern;
            var geo = CoordinateConverter.UtmToLatLon(p.Easting, p.Northing, p.UtmZone, p.IsNorthernHemisphere);
            p.Latitude = geo.Latitude;
            p.Longitude = geo.Longitude;
        }
        RecalculateAndRefresh();
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi) return;
        _appSettings.EnableSnap = ChkEnableSnap.IsChecked == true;
        _appSettings.ShowEdgeDistances = ChkShowDistances.IsChecked == true;
        _appSettings.ShowInteriorAngles = ChkShowAngles.IsChecked == true;
        _appSettings.UseGroundMeasurements = ChkUseGroundArea.IsChecked == true;
        _appSettings.FixLegacyPersianShx = ChkFixShx.IsChecked == true;
        _appSettings.UsePersianDigits = ChkPersianDigits.IsChecked == true;

        CadCanvas.EnableSnap = _appSettings.EnableSnap;
        CadCanvas.ShowCadUnderlay = ChkShowCadUnderlay.IsChecked == true;
        CadCanvas.ShowEdgeDistances = _appSettings.ShowEdgeDistances;
        CadCanvas.ShowInteriorAngles = _appSettings.ShowInteriorAngles;
        CadCanvas.UseGroundMeasurements = _appSettings.UseGroundMeasurements;
        CadCanvas.UsePersianDigits = _appSettings.UsePersianDigits;

        SatelliteMap.ShowEdgeDistances = _appSettings.ShowEdgeDistances;
        SatelliteMap.ShowInteriorAngles = _appSettings.ShowInteriorAngles;
        SatelliteMap.UseGroundMeasurements = _appSettings.UseGroundMeasurements;
        SatelliteMap.UsePersianDigits = _appSettings.UsePersianDigits;

        if (_cadModel != null)
        {
            foreach (var txt in _cadModel.Texts)
                txt.CleanedText = PersianTextHelper.CleanCadText(txt.RawText, _appSettings.FixLegacyPersianShx);
            CadCanvas.InvalidateCadCache();
        }

        SatelliteMap.InvalidateVisual();
        RecalculateAndRefresh();
    }

    #endregion

    private void RecalculateAndRefresh()
    {
        _metrics = GeometryCalculator.CalculateMetrics(_points);
        CadCanvas.SetSurveyData(_points, _metrics);
        SatelliteMap.ActiveUtmZone = ActiveUtmZone;
        SatelliteMap.ActiveIsNorthern = ActiveIsNorthern;
        SatelliteMap.SetSurveyData(_points, _metrics);

        bool fa = _appSettings.UsePersianDigits;
        TxtPointCount.Text = $"{(fa ? PersianTextHelper.ToPersianDigits(_points.Count.ToString()) : _points.Count)} نقطه";
        BannerSelfIntersect.Visibility = _metrics.HasSelfIntersection ? Visibility.Visible : Visibility.Collapsed;

        double primaryArea = _appSettings.UseGroundMeasurements ? _metrics.GroundAreaSqMeters : _metrics.PlanarAreaSqMeters;
        double primaryPerim = _appSettings.UseGroundMeasurements ? _metrics.GroundPerimeterMeters : _metrics.PlanarPerimeterMeters;

        TxtGroundArea.Text = $"{PersianTextHelper.FormatNumber(primaryArea, 2, fa)} m²";
        TxtGroundHectares.Text = $"{PersianTextHelper.FormatPlainNumber(primaryArea / 10000.0, 4, fa)} هکتار";
        TxtPerimeter.Text = $"{PersianTextHelper.FormatNumber(primaryPerim, 2, fa)} m";
        TxtPlanarAreaSub.Text = $"مسطح UTM: {PersianTextHelper.FormatNumber(_metrics.PlanarAreaSqMeters, 2, fa)} m²";
        TxtScaleFactorInfo.Text = $"ضریب مقیاس میانگین UTM: {PersianTextHelper.FormatPlainNumber(_metrics.AverageScaleFactor, 6, fa)}";

        LstEdges.ItemsSource = null;
        LstEdges.ItemsSource = _metrics.Edges;
    }

    private static bool TryParseDouble(string? text, out double val)
    {
        val = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string norm = PersianTextHelper.ToEnglishDigits(text.Trim()).Replace("،", ".").Replace(",", ".");
        return double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T target) return target;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
