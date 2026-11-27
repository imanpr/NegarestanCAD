using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NegarestanCAD.Models;
using NegarestanCAD.Services;

namespace NegarestanCAD.Controls;

/// <summary>
/// کنترل بومی و فوق‌سریع نقشه ماهواره‌ای زمین با زوم پیوسته، همگام‌سازی زنده دوطرفه با بوم CAD،
/// نمایش و درگ مستقیم نقاط محدوده، ثبت نقطه با کلیک، خط‌کش و زاویه‌سنج
/// </summary>
public class SatelliteMapControl : FrameworkElement
{
    private double _centerLat = 25.135843248135032;
    private double _centerLon = 61.18867633933179;
    private double _zoom = 16.0;

    private IReadOnlyList<SurveyPoint> _points = Array.Empty<SurveyPoint>();
    private PolygonMetrics _metrics = new();
    private readonly List<GeoCoordinate> _measurePoints = new();

    private bool _isPanning;
    private int _draggingPointIndex = -1;
    private Point _lastMouseScreen;
    private Point _currentMouseScreen;
    private bool _hasMousePosition;
    private bool _tileRefreshPending;

    public SatelliteMapProvider Provider { get; set; } = SatelliteMapProvider.GoogleHybrid;
    public CanvasToolMode ToolMode { get; set; } = CanvasToolMode.Pan;
    public bool UseGroundMeasurements { get; set; } = true;
    public bool UsePersianDigits { get; set; } = false;
    public bool ShowEdgeDistances { get; set; } = true;
    public bool ShowInteriorAngles { get; set; } = true;
    public int SelectedPointIndex { get; set; } = -1;
    public int ActiveUtmZone { get; set; } = 41;
    public bool ActiveIsNorthern { get; set; } = true;

    public double CenterLat => _centerLat;
    public double CenterLon => _centerLon;
    public double Zoom => _zoom;

    /// <summary>هنگام حرکت یا زوم توسط کاربر، مرکز (Lat, Lon) و مقیاس (متر بر پیکسل) را برای همگام‌سازی به CAD اعلام می‌کند</summary>
    public event Action<double, double, double>? ViewportChanged;
    public event Action<double, double>? CursorGeoMoved;
    public event Action<double, double>? PointClickedToAdd;
    public event Action<int, double, double>? PointDragged;
    public event Action<string>? MeasurementUpdated;

    public SatelliteMapControl()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public void SetSurveyData(IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics)
    {
        _points = points;
        _metrics = metrics;
        InvalidateVisual();
    }

    public void ClearMeasurements()
    {
        _measurePoints.Clear();
        MeasurementUpdated?.Invoke(string.Empty);
        InvalidateVisual();
    }

    public void SetView(double lat, double lon, double zoom, bool raiseEvent = false)
    {
        int maxZ = SatelliteTileService.GetMaxZoom(Provider);
        _centerLat = Math.Clamp(lat, -85.0, 85.0);
        _centerLon = Math.Clamp(lon, -180.0, 180.0);
        _zoom = Math.Clamp(zoom, 2.0, maxZ + 0.8);
        InvalidateVisual();

        if (raiseEvent)
            RaiseViewportChanged();
    }

    public void SetViewByCenterAndMetersPerPixel(double lat, double lon, double metersPerPixel, bool raiseEvent = false)
    {
        double targetZoom = SatelliteTileService.ZoomFromMetersPerPixel(lat, metersPerPixel);
        SetView(lat, lon, targetZoom, raiseEvent);
    }

    public void ZoomToSurveyPoints(bool raiseEvent = true)
    {
        if (_points.Count == 0) return;

        double minLat = _points.Min(p => p.Latitude);
        double maxLat = _points.Max(p => p.Latitude);
        double minLon = _points.Min(p => p.Longitude);
        double maxLon = _points.Max(p => p.Longitude);

        double cLat = (minLat + maxLat) * 0.5;
        double cLon = (minLon + maxLon) * 0.5;

        double w = ActualWidth > 20 ? ActualWidth : 700;
        double h = ActualHeight > 20 ? ActualHeight : 500;

        if (Math.Abs(maxLat - minLat) < 1e-6 && Math.Abs(maxLon - minLon) < 1e-6)
        {
            SetView(cLat, cLon, 18.0, raiseEvent);
            return;
        }

        // محاسبه زوم مناسب برای جا شدن کامل محدوده
        double latSpanMeters = Math.Max(20.0, (maxLat - minLat) * 111320.0);
        double lonSpanMeters = Math.Max(20.0, (maxLon - minLon) * 111320.0 * Math.Cos(cLat * Math.PI / 180.0));
        double mpp = Math.Max(lonSpanMeters / (w * 0.78), latSpanMeters / (h * 0.78));
        SetViewByCenterAndMetersPerPixel(cLat, cLon, mpp, raiseEvent);
    }

    public Point GeoToScreen(double lat, double lon)
    {
        double w = ActualWidth > 1 ? ActualWidth : 600;
        double h = ActualHeight > 1 ? ActualHeight : 500;
        var (cpx, cpy) = SatelliteTileService.LatLonToWorldPixels(_centerLat, _centerLon, _zoom);
        var (px, py) = SatelliteTileService.LatLonToWorldPixels(lat, lon, _zoom);
        return new Point(w * 0.5 + (px - cpx), h * 0.5 + (py - cpy));
    }

    public GeoCoordinate ScreenToGeo(Point sp)
    {
        double w = ActualWidth > 1 ? ActualWidth : 600;
        double h = ActualHeight > 1 ? ActualHeight : 500;
        var (cpx, cpy) = SatelliteTileService.LatLonToWorldPixels(_centerLat, _centerLon, _zoom);
        double px = cpx + (sp.X - w * 0.5);
        double py = cpy + (sp.Y - h * 0.5);
        var (lat, lon) = SatelliteTileService.WorldPixelsToLatLon(px, py, _zoom);
        return new GeoCoordinate(lat, lon);
    }

    private void RaiseViewportChanged()
    {
        double mpp = SatelliteTileService.MetersPerPixel(_centerLat, _zoom);
        ViewportChanged?.Invoke(_centerLat, _centerLon, mpp);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Point mousePos = e.GetPosition(this);
        var geoBefore = ScreenToGeo(mousePos);

        int maxZ = SatelliteTileService.GetMaxZoom(Provider);
        double deltaZoom = e.Delta > 0 ? 0.35 : -0.35;
        _zoom = Math.Clamp(_zoom + deltaZoom, 2.0, maxZ + 0.8);

        // ثابت نگه داشتن نقطه زیر مکان‌نما هنگام زوم
        double w = ActualWidth > 1 ? ActualWidth : 600;
        double h = ActualHeight > 1 ? ActualHeight : 500;
        var (gpx, gpy) = SatelliteTileService.LatLonToWorldPixels(geoBefore.Latitude, geoBefore.Longitude, _zoom);
        double newCenterPx = gpx - (mousePos.X - w * 0.5);
        double newCenterPy = gpy - (mousePos.Y - h * 0.5);
        var (newLat, newLon) = SatelliteTileService.WorldPixelsToLatLon(newCenterPx, newCenterPy, _zoom);
        _centerLat = newLat;
        _centerLon = newLon;

        InvalidateVisual();
        RaiseViewportChanged();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Point pos = e.GetPosition(this);

        // بررسی کلیک روی نقاط محدوده برای درگ کردن روی نقشه ماهواره‌ای
        if (e.ChangedButton == MouseButton.Left &&
            (ToolMode == CanvasToolMode.Pan || ToolMode == CanvasToolMode.AddPoint))
        {
            int hitIdx = FindSurveyPointAt(pos, 14.0);
            if (hitIdx >= 0)
            {
                _draggingPointIndex = hitIdx;
                SelectedPointIndex = hitIdx;
                CaptureMouse();
                Cursor = Cursors.SizeAll;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (e.ChangedButton == MouseButton.Middle ||
            e.ChangedButton == MouseButton.Right ||
            (e.ChangedButton == MouseButton.Left && ToolMode == CanvasToolMode.Pan))
        {
            _isPanning = true;
            _lastMouseScreen = pos;
            CaptureMouse();
            Cursor = Cursors.Hand;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            var geo = ScreenToGeo(pos);
            if (ToolMode == CanvasToolMode.AddPoint)
            {
                PointClickedToAdd?.Invoke(geo.Latitude, geo.Longitude);
            }
            else if (ToolMode == CanvasToolMode.Ruler)
            {
                if (_measurePoints.Count >= 2) _measurePoints.Clear();
                _measurePoints.Add(geo);
                UpdateMeasurementText();
                InvalidateVisual();
            }
            else if (ToolMode == CanvasToolMode.AngleMeasure)
            {
                if (_measurePoints.Count >= 3) _measurePoints.Clear();
                _measurePoints.Add(geo);
                UpdateMeasurementText();
                InvalidateVisual();
            }
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pos = e.GetPosition(this);
        _currentMouseScreen = pos;
        _hasMousePosition = true;

        if (_isPanning)
        {
            double dx = pos.X - _lastMouseScreen.X;
            double dy = pos.Y - _lastMouseScreen.Y;
            _lastMouseScreen = pos;

            var (cpx, cpy) = SatelliteTileService.LatLonToWorldPixels(_centerLat, _centerLon, _zoom);
            var (newLat, newLon) = SatelliteTileService.WorldPixelsToLatLon(cpx - dx, cpy - dy, _zoom);
            _centerLat = newLat;
            _centerLon = newLon;

            InvalidateVisual();
            RaiseViewportChanged();
            return;
        }

        if (_draggingPointIndex >= 0)
        {
            var geo = ScreenToGeo(pos);
            PointDragged?.Invoke(_draggingPointIndex, geo.Latitude, geo.Longitude);
            CursorGeoMoved?.Invoke(geo.Latitude, geo.Longitude);
            return;
        }

        Cursor = (ToolMode == CanvasToolMode.Pan || ToolMode == CanvasToolMode.AddPoint) && FindSurveyPointAt(pos, 14.0) >= 0
            ? Cursors.SizeAll
            : Cursors.Cross;

        var curGeo = ScreenToGeo(pos);
        CursorGeoMoved?.Invoke(curGeo.Latitude, curGeo.Longitude);

        if (_measurePoints.Count > 0)
            InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_draggingPointIndex >= 0)
        {
            _draggingPointIndex = -1;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
            return;
        }
        if (_isPanning)
        {
            _isPanning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hasMousePosition = false;
        InvalidateVisual();
    }

    private int FindSurveyPointAt(Point screenPos, double hitRadiusPx)
    {
        double bestDistSq = hitRadiusPx * hitRadiusPx;
        int bestIdx = -1;
        for (int i = 0; i < _points.Count; i++)
        {
            Point sp = GeoToScreen(_points[i].Latitude, _points[i].Longitude);
            double d2 = (sp.X - screenPos.X) * (sp.X - screenPos.X) + (sp.Y - screenPos.Y) * (sp.Y - screenPos.Y);
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private void UpdateMeasurementText()
    {
        if (ToolMode == CanvasToolMode.Ruler && _measurePoints.Count == 2)
        {
            var u1 = CoordinateConverter.LatLonToUtm(_measurePoints[0].Latitude, _measurePoints[0].Longitude, ActiveUtmZone);
            var u2 = CoordinateConverter.LatLonToUtm(_measurePoints[1].Latitude, _measurePoints[1].Longitude, ActiveUtmZone);
            double dx = u2.Easting - u1.Easting;
            double dy = u2.Northing - u1.Northing;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double az = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (az < 0) az += 360.0;
            MeasurementUpdated?.Invoke($"خط‌کش ماهواره: فاصله = {PersianTextHelper.FormatNumber(dist, 2, UsePersianDigits)} متر | آزیموت = {PersianTextHelper.FormatPlainNumber(az, 1, UsePersianDigits)}°");
        }
        else if (ToolMode == CanvasToolMode.AngleMeasure && _measurePoints.Count == 3)
        {
            var a = CoordinateConverter.LatLonToUtm(_measurePoints[0].Latitude, _measurePoints[0].Longitude, ActiveUtmZone);
            var v = CoordinateConverter.LatLonToUtm(_measurePoints[1].Latitude, _measurePoints[1].Longitude, ActiveUtmZone);
            var b = CoordinateConverter.LatLonToUtm(_measurePoints[2].Latitude, _measurePoints[2].Longitude, ActiveUtmZone);
            double v1x = a.Easting - v.Easting, v1y = a.Northing - v.Northing;
            double v2x = b.Easting - v.Easting, v2y = b.Northing - v.Northing;
            double l1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (l1 > 1e-9 && l2 > 1e-9)
            {
                double dot = Math.Clamp((v1x * v2x + v1y * v2y) / (l1 * l2), -1.0, 1.0);
                double deg = Math.Acos(dot) * 180.0 / Math.PI;
                MeasurementUpdated?.Invoke($"زاویه‌سنج ماهواره: زاویه = {PersianTextHelper.FormatPlainNumber(deg, 2, UsePersianDigits)}°");
            }
        }
    }

    private void RequestTileRepaint()
    {
        if (_tileRefreshPending) return;
        _tileRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _tileRefreshPending = false;
            InvalidateVisual();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 1. پس‌زمینه تیره هنگام بارگذاری تایل‌ها
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(0, 0, w, h));

        // 2. رندر تایل‌های نقشه ماهواره‌ای با زوم پیوسته (Fractional Zoom)
        int maxZ = SatelliteTileService.GetMaxZoom(Provider);
        int tileZ = Math.Clamp((int)Math.Floor(_zoom), 2, maxZ);
        double zoomScale = Math.Pow(2.0, _zoom - tileZ);
        double tileScreenSize = 256.0 * zoomScale;

        var (cpx, cpy) = SatelliteTileService.LatLonToWorldPixels(_centerLat, _centerLon, tileZ);
        double topLeftTileX = (cpx - (w * 0.5) / zoomScale) / 256.0;
        double topLeftTileY = (cpy - (h * 0.5) / zoomScale) / 256.0;
        double botRightTileX = (cpx + (w * 0.5) / zoomScale) / 256.0;
        double botRightTileY = (cpy + (h * 0.5) / zoomScale) / 256.0;

        int minTx = (int)Math.Floor(topLeftTileX);
        int maxTx = (int)Math.Floor(botRightTileX);
        int minTy = (int)Math.Floor(topLeftTileY);
        int maxTy = (int)Math.Floor(botRightTileY);
        int maxIdx = 1 << tileZ;

        for (int tx = minTx; tx <= maxTx; tx++)
        {
            for (int ty = minTy; ty <= maxTy; ty++)
            {
                if (ty < 0 || ty >= maxIdx) continue;
                int wrappedX = ((tx % maxIdx) + maxIdx) % maxIdx;

                double sx = w * 0.5 + (tx * 256.0 - cpx) * zoomScale;
                double sy = h * 0.5 + (ty * 256.0 - cpy) * zoomScale;
                var tileRect = new Rect(Math.Floor(sx), Math.Floor(sy), Math.Ceiling(tileScreenSize) + 0.8, Math.Ceiling(tileScreenSize) + 0.8);

                var bmp = SatelliteTileService.TryGetTile(Provider, tileZ, wrappedX, ty, RequestTileRepaint);
                if (bmp != null)
                {
                    dc.DrawImage(bmp, tileRect);
                }
                else
                {
                    // Fallback نرم از تایل والد در حین دانلود
                    if (tileZ > 2)
                    {
                        var parentBmp = SatelliteTileService.TryGetTile(Provider, tileZ - 1, wrappedX / 2, ty / 2);
                        if (parentBmp != null)
                        {
                            double qx = (wrappedX % 2) * 128.0;
                            double qy = (ty % 2) * 128.0;
                            dc.PushClip(new RectangleGeometry(tileRect));
                            var expanded = new Rect(tileRect.X - (qx / 128.0) * tileRect.Width,
                                                    tileRect.Y - (qy / 128.0) * tileRect.Height,
                                                    tileRect.Width * 2.0, tileRect.Height * 2.0);
                            dc.DrawImage(parentBmp, expanded);
                            dc.Pop();
                            continue;
                        }
                    }

                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 32, 48)),
                        new Pen(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 0.5), tileRect);
                }
            }
        }

        // 3. رندر محدوده و نقاط UTM روی نقشه ماهواره‌ای
        RenderSurveyOverlay(dc);

        // 4. خط‌کش و زاویه‌سنج
        RenderMeasureOverlay(dc);

        // 5. برچسب منبع و سطح زوم در گوشه پایین-راست
        string info = $"{SatelliteTileService.GetAttributionText(Provider)}  |  Zoom {_zoom:F1}";
        var ft = CreateFormattedText(info, 10, Color.FromRgb(226, 232, 240), FontWeights.Normal);
        var infoRect = new Rect(w - ft.Width - 18, h - ft.Height - 10, ft.Width + 12, ft.Height + 6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(180, 15, 23, 42)), null, infoRect, 4, 4);
        dc.DrawText(ft, new Point(infoRect.X + 6, infoRect.Y + 3));
    }

    private void RenderSurveyOverlay(DrawingContext dc)
    {
        if (_points.Count == 0) return;

        var screenPts = _points.Select(p => GeoToScreen(p.Latitude, p.Longitude)).ToList();

        if (screenPts.Count >= 2)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(screenPts[0], screenPts.Count >= 3, screenPts.Count >= 3);
                ctx.PolyLineTo(screenPts.Skip(1).ToList(), true, true);
            }
            geo.Freeze();

            Brush? fill = screenPts.Count >= 3
                ? new SolidColorBrush(_metrics.HasSelfIntersection
                    ? Color.FromArgb(70, 239, 68, 68)
                    : Color.FromArgb(65, 14, 165, 233))
                : null;
            var stroke = _metrics.HasSelfIntersection
                ? Color.FromRgb(248, 113, 113)
                : Color.FromRgb(250, 204, 21);
            var pen = new Pen(new SolidColorBrush(stroke), 2.6);
            pen.Freeze();

            dc.DrawGeometry(fill, pen, geo);
        }

        if (ShowEdgeDistances && _metrics.Edges.Count > 0)
        {
            foreach (var edge in _metrics.Edges)
            {
                var midGeo = CoordinateConverter.UtmToLatLon(edge.MidX, edge.MidY, ActiveUtmZone, ActiveIsNorthern);
                Point mid = GeoToScreen(midGeo.Latitude, midGeo.Longitude);
                double len = UseGroundMeasurements ? edge.GroundLengthMeters : edge.PlanarLengthMeters;
                DrawBadge(dc, mid, $"{PersianTextHelper.FormatNumber(len, 2, UsePersianDigits)} m",
                    Color.FromArgb(220, 15, 23, 42), Color.FromRgb(250, 204, 21), 11);
            }
        }

        if (_metrics.IsPolygonClosed)
        {
            var cGeo = CoordinateConverter.UtmToLatLon(_metrics.CentroidX, _metrics.CentroidY, ActiveUtmZone, ActiveIsNorthern);
            Point center = GeoToScreen(cGeo.Latitude, cGeo.Longitude);
            double area = UseGroundMeasurements ? _metrics.GroundAreaSqMeters : _metrics.PlanarAreaSqMeters;
            DrawBadge(dc, center, $"مساحت: {PersianTextHelper.FormatNumber(area, 2, UsePersianDigits)} m²",
                Color.FromArgb(230, 2, 132, 199), Colors.White, 12.5, FontWeights.Bold);
        }

        for (int i = 0; i < _points.Count; i++)
        {
            Point sp = screenPts[i];
            bool isSelected = i == SelectedPointIndex;
            double r = isSelected ? 7.5 : 5.5;
            dc.DrawEllipse(isSelected ? Brushes.OrangeRed : new SolidColorBrush(Color.FromRgb(244, 63, 94)),
                new Pen(Brushes.White, 2.0), sp, r, r);

            DrawBadge(dc, new Point(sp.X + 16, sp.Y - 16), _points[i].Label,
                isSelected ? Color.FromRgb(234, 88, 12) : Color.FromRgb(15, 23, 42),
                Colors.White, 12, FontWeights.Bold);
        }
    }

    private void RenderMeasureOverlay(DrawingContext dc)
    {
        if (_measurePoints.Count == 0) return;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(34, 197, 94)), 2.2) { DashStyle = DashStyles.Dash };
        var screenPts = _measurePoints.Select(p => GeoToScreen(p.Latitude, p.Longitude)).ToList();

        for (int i = 0; i < screenPts.Count - 1; i++)
            dc.DrawLine(pen, screenPts[i], screenPts[i + 1]);

        if (_hasMousePosition &&
            ((ToolMode == CanvasToolMode.Ruler && _measurePoints.Count == 1) ||
             (ToolMode == CanvasToolMode.AngleMeasure && _measurePoints.Count is 1 or 2)))
        {
            dc.DrawLine(pen, screenPts[^1], _currentMouseScreen);
        }

        foreach (var sp in screenPts)
            dc.DrawEllipse(Brushes.LimeGreen, new Pen(Brushes.White, 1.5), sp, 4.5, 4.5);
    }

    private void DrawBadge(DrawingContext dc, Point center, string text, Color bg, Color fg, double fontSize, FontWeight? weight = null)
    {
        var ft = CreateFormattedText(text, fontSize, fg, weight ?? FontWeights.SemiBold);
        double padX = 6, padY = 2.5;
        var rect = new Rect(center.X - ft.Width * 0.5 - padX, center.Y - ft.Height * 0.5 - padY,
                            ft.Width + padX * 2, ft.Height + padY * 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 0.8), rect, 4, 4);
        dc.DrawText(ft, new Point(rect.X + padX, rect.Y + padY));
    }

    private FormattedText CreateFormattedText(string text, double fontSize, Color color, FontWeight weight)
    {
        var typeface = new Typeface(App.PersianFontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize,
            new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
