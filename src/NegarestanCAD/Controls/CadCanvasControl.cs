using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NegarestanCAD.Models;
using NegarestanCAD.Services;
using System.Windows.Media.Imaging;

namespace NegarestanCAD.Controls;

public enum CanvasToolMode
{
    Pan,
    AddPoint,
    Ruler,
    AngleMeasure
}




/// <summary>
/// بوم تعاملی فوق‌سریع برای نمایش فایل‌های DWG/DXF، نقاط UTM، محدوده، فواصل، مساحت، Snap، خط‌کش و زاویه‌سنج
/// </summary>
public class CadCanvasControl : FrameworkElement
{
    private CadRenderModel? _cadModel;
    private IReadOnlyList<SurveyPoint> _points = Array.Empty<SurveyPoint>();
    private PolygonMetrics _metrics = new();
    private HashSet<string> _visibleLayers = new(StringComparer.OrdinalIgnoreCase);

    // تبدیل مختصات جهان (UTM) به صفحه (Screen) با دقت double
    private double _originX;
    private double _originY;
    private double _scale = 1.0; // پیکسل بر متر
    private double _panOffsetX;
    private double _panOffsetY;

    // وضعیت موس و Pan
    private bool _isPanning;
    private Point _lastMouseScreen;
    private Point _currentMouseScreen;
    private bool _hasMousePosition;
    private SnapCandidate? _activeSnap;

    // ابزار خط‌کش و زاویه‌سنج
    private readonly List<WorldPoint> _measurePoints = new();

    public CanvasToolMode ToolMode { get; set; } = CanvasToolMode.Pan;
    public bool IsDarkBackground { get; set; } = true;
    public bool EnableSnap { get; set; } = true;
    public bool UseGroundMeasurements { get; set; } = true;
    public bool UsePersianDigits { get; set; } = false;
    public bool ShowEdgeDistances { get; set; } = true;
    public bool ShowInteriorAngles { get; set; } = true;
    public bool ShowCadUnderlay { get; set; } = true;
    public int SelectedPointIndex { get; set; } = -1;

    public event Action<double, double, SnapCandidate?>? CursorWorldMoved;
    public event Action<double, double>? PointClickedToAdd;
    public event Action<string>? MeasurementUpdated;


    // کش تصویری بوم CAD برای حرکت و اسنپ ۶۰ فریم روی نقشه‌های سنگین
    private RenderTargetBitmap? _cadBitmapCache;
    private bool _cadCacheDirty = true;
    private double _cachePanX;
    private double _cachePanY;
    private double _cacheScale;


    private int _draggingPointIndex = -1;
    public event Action<int, double, double>? PointDragged;


    public void InvalidateCadCache()
    {
        _cadCacheDirty = true;
        InvalidateVisual();
    }


    public CadCanvasControl()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public void SetCadModel(CadRenderModel? model)
    {
        _cadModel = model;
        RefreshVisibleLayers();
        if (model != null && model.HasGeometry)
            ZoomToExtents();
        else
            InvalidateVisual();
    }

    public void RefreshVisibleLayers()
    {
        _visibleLayers = _cadModel == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : _cadModel.Layers.Where(l => l.IsVisible).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        InvalidateCadCache();
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

    public void ZoomToExtents()
    {
        var xs = new List<double>();
        var ys = new List<double>();

        if (_cadModel != null && ShowCadUnderlay)
        {
            foreach (var poly in _cadModel.Polylines)
            {
                if (poly.IsFilled || poly.Points.Count == 0 || !_visibleLayers.Contains(poly.LayerName)) continue;
                xs.Add(poly.Points[0].X);
                ys.Add(poly.Points[0].Y);
                if (poly.Points.Count > 1)
                {
                    xs.Add(poly.Points[^1].X);
                    ys.Add(poly.Points[^1].Y);
                }
            }
        }

        foreach (var p in _points)
        {
            xs.Add(p.Easting);
            ys.Add(p.Northing);
        }

        if (xs.Count == 0)
        {
            InvalidateVisual();
            return;
        }

        xs.Sort();
        ys.Sort();

        // حذف نقاط پرت (مثل 0,0 یا المان‌های خیلی دور از کادر اصلی نقشه)
        double minX, maxX, minY, maxY;
        if (xs.Count >= 40)
        {
            double q1x = xs[(int)(xs.Count * 0.05)], q3x = xs[(int)(xs.Count * 0.95)];
            double q1y = ys[(int)(ys.Count * 0.05)], q3y = ys[(int)(ys.Count * 0.95)];
            double iqrX = Math.Max(10.0, q3x - q1x);
            double iqrY = Math.Max(10.0, q3y - q1y);

            double loX = q1x - 3.0 * iqrX, hiX = q3x + 3.0 * iqrX;
            double loY = q1y - 3.0 * iqrY, hiY = q3y + 3.0 * iqrY;

            var fx = xs.Where(v => v >= loX && v <= hiX).ToList();
            var fy = ys.Where(v => v >= loY && v <= hiY).ToList();

            minX = fx.Count > 0 ? fx[0] : xs[0];
            maxX = fx.Count > 0 ? fx[^1] : xs[^1];
            minY = fy.Count > 0 ? fy[0] : ys[0];
            maxY = fy.Count > 0 ? fy[^1] : ys[^1];
        }
        else
        {
            minX = xs[0]; maxX = xs[^1];
            minY = ys[0]; maxY = ys[^1];
        }

        FitBounds(minX, minY, maxX, maxY);
    }

    public void ZoomToSurveyPoints()
    {
        if (_points.Count == 0)
        {
            ZoomToExtents();
            return;
        }

        double minX = _points.Min(p => p.Easting);
        double minY = _points.Min(p => p.Northing);
        double maxX = _points.Max(p => p.Easting);
        double maxY = _points.Max(p => p.Northing);

        if (Math.Abs(maxX - minX) < 1e-6 && Math.Abs(maxY - minY) < 1e-6)
        {
            minX -= 25; maxX += 25;
            minY -= 25; maxY += 25;
        }

        FitBounds(minX, minY, maxX, maxY);
    }

    private void FitBounds(double minX, double minY, double maxX, double maxY)
    {
        double w = ActualWidth > 10 ? ActualWidth : 800;
        double h = ActualHeight > 10 ? ActualHeight : 600;

        double spanX = Math.Max(1.0, maxX - minX);
        double spanY = Math.Max(1.0, maxY - minY);

        _originX = (minX + maxX) * 0.5;
        _originY = (minY + maxY) * 0.5;
        _scale = Math.Min((w * 0.84) / spanX, (h * 0.84) / spanY);
        _panOffsetX = w * 0.5;
        _panOffsetY = h * 0.5;

        InvalidateCadCache();
    }

    public Point WorldToScreen(double wx, double wy)
    {
        double sx = (wx - _originX) * _scale + _panOffsetX;
        double sy = -(wy - _originY) * _scale + _panOffsetY;
        return new Point(sx, sy);
    }

    public WorldPoint ScreenToWorld(Point sp)
    {
        double wx = (sp.X - _panOffsetX) / _scale + _originX;
        double wy = -((sp.Y - _panOffsetY) / _scale) + _originY;
        return new WorldPoint(wx, wy);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Point mousePos = e.GetPosition(this);
        WorldPoint worldBefore = ScreenToWorld(mousePos);

        double factor = e.Delta > 0 ? 1.22 : (1.0 / 1.22);
        _scale = Math.Clamp(_scale * factor, 1e-5, 1e6);

        // ثابت نگه داشتن نقطه زیر مکان‌نما هنگام زوم
        _panOffsetX = mousePos.X - (worldBefore.X - _originX) * _scale;
        _panOffsetY = mousePos.Y + (worldBefore.Y - _originY) * _scale;

        InvalidateCadCache();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        Point pos = e.GetPosition(this);


        // بررسی کلیک روی یکی از نقاط محدوده (A, B, C...) برای جابجایی با درگ
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
            if (e.ChangedButton == MouseButton.Middle && e.ClickCount == 2)
            {
                ZoomToExtents();
                return;
            }
            _isPanning = true;
            _lastMouseScreen = pos;
            CaptureMouse();
            Cursor = Cursors.Hand;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            WorldPoint target = GetSnappedOrRawWorld(pos);

            if (ToolMode == CanvasToolMode.AddPoint)
            {
                PointClickedToAdd?.Invoke(target.X, target.Y);
            }
            else if (ToolMode == CanvasToolMode.Ruler)
            {
                if (_measurePoints.Count >= 2) _measurePoints.Clear();
                _measurePoints.Add(target);
                UpdateMeasurementText();
                InvalidateVisual();
            }
            else if (ToolMode == CanvasToolMode.AngleMeasure)
            {
                if (_measurePoints.Count >= 3) _measurePoints.Clear();
                _measurePoints.Add(target);
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
            _panOffsetX += (pos.X - _lastMouseScreen.X);
            _panOffsetY += (pos.Y - _lastMouseScreen.Y);
            _lastMouseScreen = pos;
            InvalidateVisual();
            return;
        }


        if (_draggingPointIndex >= 0)
        {
            _activeSnap = EnableSnap ? FindNearestSnap(pos, 14.0, _draggingPointIndex) : null;
            WorldPoint target = _activeSnap.HasValue
                ? new WorldPoint(_activeSnap.Value.X, _activeSnap.Value.Y)
                : ScreenToWorld(pos);

            PointDragged?.Invoke(_draggingPointIndex, target.X, target.Y);
            CursorWorldMoved?.Invoke(target.X, target.Y, _activeSnap);
            return;
        }

        // تغییر شکل مکان‌نما وقتی روی یک نقطه محدوده قرار می‌گیرد
        Cursor = (ToolMode == CanvasToolMode.Pan || ToolMode == CanvasToolMode.AddPoint) && FindSurveyPointAt(pos, 14.0) >= 0
            ? Cursors.SizeAll
            : Cursors.Cross;


        _activeSnap = EnableSnap ? FindNearestSnap(pos, 14.0) : null;
        WorldPoint world = _activeSnap.HasValue
            ? new WorldPoint(_activeSnap.Value.X, _activeSnap.Value.Y)
            : ScreenToWorld(pos);

        CursorWorldMoved?.Invoke(world.X, world.Y, _activeSnap);

        if (EnableSnap || _measurePoints.Count > 0)
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
            InvalidateCadCache();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateCadCache();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hasMousePosition = false;
        _activeSnap = null;
        InvalidateVisual();
    }

    private WorldPoint GetSnappedOrRawWorld(Point screenPos)
    {
        if (EnableSnap)
        {
            var snap = FindNearestSnap(screenPos, 14.0);
            if (snap.HasValue)
                return new WorldPoint(snap.Value.X, snap.Value.Y);
        }
        return ScreenToWorld(screenPos);
    }

    private SnapCandidate? FindNearestSnap(Point screenPos, double tolerancePx, int excludeSurveyIndex = -1)
    {
        double bestDistSq = tolerancePx * tolerancePx;
        SnapCandidate? best = null;


        // اولویت اول: نقاط نقشه‌برداری واردشده (به‌جز نقطه‌ای که الان در حال درگ است)
        for (int i = 0; i < _points.Count; i++)
        {
            if (i == excludeSurveyIndex) continue;
            var pt = _points[i];
            Point sp = WorldToScreen(pt.Easting, pt.Northing);
            double d2 = (sp.X - screenPos.X) * (sp.X - screenPos.X) + (sp.Y - screenPos.Y) * (sp.Y - screenPos.Y);
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                best = new SnapCandidate(pt.Easting, pt.Northing, SnapPointKind.Endpoint, "SURVEY");
            }
        }


        //// اولویت اول: نقاط نقشه‌برداری واردشده
        //foreach (var pt in _points)
        //{
        //    Point sp = WorldToScreen(pt.Easting, pt.Northing);
        //    double d2 = (sp.X - screenPos.X) * (sp.X - screenPos.X) + (sp.Y - screenPos.Y) * (sp.Y - screenPos.Y);
        //    if (d2 < bestDistSq)
        //    {
        //        bestDistSq = d2;
        //        best = new SnapCandidate(pt.Easting, pt.Northing, SnapPointKind.Endpoint, "SURVEY");
        //    }
        //}

        // اولویت دوم: نقاط Snap نقشه DWG
        if (_cadModel != null && ShowCadUnderlay && _cadModel.SnapPoints.Count > 0)
        {
            double tolWorld = tolerancePx / _scale;
            WorldPoint mw = ScreenToWorld(screenPos);

            foreach (var cand in _cadModel.SnapPoints)
            {
                if (Math.Abs(cand.X - mw.X) > tolWorld || Math.Abs(cand.Y - mw.Y) > tolWorld) continue;
                if (!_visibleLayers.Contains(cand.LayerName)) continue;

                Point sp = WorldToScreen(cand.X, cand.Y);
                double d2 = (sp.X - screenPos.X) * (sp.X - screenPos.X) + (sp.Y - screenPos.Y) * (sp.Y - screenPos.Y);
                if (d2 < bestDistSq)
                {
                    bestDistSq = d2;
                    best = cand;
                }
            }
        }

        return best;
    }


    private int FindSurveyPointAt(Point screenPos, double hitRadiusPx)
    {
        double bestDistSq = hitRadiusPx * hitRadiusPx;
        int bestIdx = -1;
        for (int i = 0; i < _points.Count; i++)
        {
            Point sp = WorldToScreen(_points[i].Easting, _points[i].Northing);
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
            double dx = _measurePoints[1].X - _measurePoints[0].X;
            double dy = _measurePoints[1].Y - _measurePoints[0].Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double az = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (az < 0) az += 360.0;
            MeasurementUpdated?.Invoke($"خط‌کش آزاد: فاصله = {PersianTextHelper.FormatNumber(dist, 3, UsePersianDigits)} متر | آزیموت = {PersianTextHelper.FormatPlainNumber(az, 2, UsePersianDigits)}°");
        }
        else if (ToolMode == CanvasToolMode.AngleMeasure && _measurePoints.Count == 3)
        {
            var a = _measurePoints[0];
            var v = _measurePoints[1];
            var b = _measurePoints[2];
            double v1x = a.X - v.X, v1y = a.Y - v.Y;
            double v2x = b.X - v.X, v2y = b.Y - v.Y;
            double l1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (l1 > 1e-9 && l2 > 1e-9)
            {
                double dot = Math.Clamp((v1x * v2x + v1y * v2y) / (l1 * l2), -1.0, 1.0);
                double deg = Math.Acos(dot) * 180.0 / Math.PI;
                MeasurementUpdated?.Invoke($"زاویه‌سنج: زاویه = {PersianTextHelper.FormatPlainNumber(deg, 2, UsePersianDigits)}°");
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 1. پس‌زمینه بوم CAD
        var bgBrush = IsDarkBackground
            ? new SolidColorBrush(Color.FromRgb(18, 22, 29))
            : new SolidColorBrush(Color.FromRgb(248, 250, 252));
        dc.DrawRectangle(bgBrush, null, new Rect(0, 0, w, h));

        DrawSubtleGrid(dc, w, h);

        // 2. رندر نقشه DWG/DXF
        //if (_cadModel != null && ShowCadUnderlay)
        //{
        //    RenderCadEntities(dc, w, h);
        //}
        // 2. رندر نقشه DWG/DXF با کش هوشمند
        if (_cadModel != null && ShowCadUnderlay)
        {
            if (_isPanning && _cadBitmapCache != null && Math.Abs(_scale - _cacheScale) < 1e-9)
            {
                double dx = _panOffsetX - _cachePanX;
                double dy = _panOffsetY - _cachePanY;
                dc.DrawImage(_cadBitmapCache, new Rect(dx, dy, w, h));
            }
            else
            {
                if (_cadCacheDirty || _cadBitmapCache == null)
                    RebuildCadBitmapCache(w, h);

                if (_cadBitmapCache != null)
                    dc.DrawImage(_cadBitmapCache, new Rect(0, 0, w, h));
            }
        }




        // 3. رندر محدوده و نقاط UTM
        RenderSurveyPolygonAndPoints(dc);

        // 4. رندر ابزار خط‌کش / زاویه‌سنج
        RenderMeasurementOverlay(dc);

        // 5. نشانگر Object Snap زنده
        if (_activeSnap.HasValue)
        {
            RenderSnapIndicator(dc, _activeSnap.Value);
        }

        // 6. پیام راهنما در صورت خالی بودن صفحه
        if ((_cadModel == null || !_cadModel.HasGeometry) && _points.Count == 0)
        {
            var hintColor = IsDarkBackground ? Color.FromRgb(140, 155, 175) : Color.FromRgb(100, 116, 139);
            var ft = CreateFormattedText("برای شروع، یک فایل DWG / DXF باز کنید یا مختصات UTM / Lat-Lon وارد نمایید", 14, hintColor, FontWeights.Medium);
            dc.DrawText(ft, new Point((w - ft.Width) * 0.5, (h - ft.Height) * 0.5));
        }
    }


    private void RebuildCadBitmapCache(double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int pxW = Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX));
        int pxH = Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY));

        var dv = new DrawingVisual();
        using (var vdc = dv.RenderOpen())
        {
            RenderCadEntities(vdc, w, h);
        }

        var rtb = new RenderTargetBitmap(pxW, pxH, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        _cadBitmapCache = rtb;
        _cachePanX = _panOffsetX;
        _cachePanY = _panOffsetY;
        _cacheScale = _scale;
        _cadCacheDirty = false;
    }


    private void DrawSubtleGrid(DrawingContext dc, double w, double h)
    {
        var gridColor = IsDarkBackground ? Color.FromArgb(26, 148, 163, 184) : Color.FromArgb(30, 100, 116, 139);
        var pen = new Pen(new SolidColorBrush(gridColor), 0.7);
        pen.Freeze();

        double stepPx = 60.0;
        for (double x = _panOffsetX % stepPx; x < w; x += stepPx)
            dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
        for (double y = _panOffsetY % stepPx; y < h; y += stepPx)
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
    }

    private void RenderCadEntities(DrawingContext dc, double w, double h)
    {
        if (_cadModel == null) return;

        var penCache = new Dictionary<uint, Pen>();
        Pen GetPen(RgbColor rgb, bool isDefaultWhite)
        {
            Color c = ResolveThemeColor(rgb, isDefaultWhite);
            uint key = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            if (!penCache.TryGetValue(key, out var pen))
            {
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                pen = new Pen(brush, 1.0);
                pen.Freeze();
                penCache[key] = pen;
            }
            return pen;
        }




        var fillCache = new Dictionary<ulong, Brush>();
        Brush GetFillBrush(RgbColor rgb, bool isDefaultWhite, bool isSolid = true)
        {
            Color c = ResolveThemeColor(rgb, isDefaultWhite);
            ulong key = ((ulong)(isSolid ? 1 : 0) << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            if (!fillCache.TryGetValue(key, out var brush))
            {
                if (isSolid)
                {
                    brush = new SolidColorBrush(Color.FromArgb(230, c.R, c.G, c.B));
                }
                else
                {
                    // الگوی هاشور مورب شبیه ANSI31 اتوکد
                    var dg = new DrawingGroup();
                    dg.Children.Add(new GeometryDrawing(
                        new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B)), null,
                        new RectangleGeometry(new Rect(0, 0, 8, 8))));
                    dg.Children.Add(new GeometryDrawing(
                        null, new Pen(new SolidColorBrush(Color.FromArgb(170, c.R, c.G, c.B)), 0.9),
                        new LineGeometry(new Point(0, 8), new Point(8, 0))));
                    dg.Freeze();

                    brush = new DrawingBrush(dg)
                    {
                        TileMode = TileMode.Tile,
                        Viewport = new Rect(0, 0, 8, 8),
                        ViewportUnits = BrushMappingMode.Absolute
                    };
                }
                brush.Freeze();
                fillCache[key] = brush;
            }
            return brush;
        }

        foreach (var hatch in _cadModel.Hatches)
        {
            if (!_visibleLayers.Contains(hatch.LayerName) || hatch.Loops.Count == 0) continue;

            var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
            bool hasLoop = false;
            using (var ctx = geo.Open())
            {
                foreach (var loop in hatch.Loops)
                {
                    if (loop.Count < 3) continue;
                    var sPts = new Point[loop.Count];
                    for (int i = 0; i < loop.Count; i++)
                        sPts[i] = WorldToScreen(loop[i].X, loop[i].Y);

                    ctx.BeginFigure(sPts[0], isFilled: true, isClosed: true);
                    ctx.PolyLineTo(sPts.Skip(1).ToArray(), isStroked: false, isSmoothJoin: false);
                    hasLoop = true;
                }
            }
            if (!hasLoop) continue;
            geo.Freeze();
            dc.DrawGeometry(GetFillBrush(hatch.Color, hatch.IsDefaultWhite, hatch.IsSolid), null, geo);
        }


        // گذر اول: رسم سطوح رنگی (Hatch / Solid) زیر خطوط
        // گذر دوم: رسم خطوط و چندخطی‌ها روی سطوح رنگی
        for (int pass = 0; pass < 2; pass++)
        {
            bool drawFilledPass = (pass == 0);
            foreach (var poly in _cadModel.Polylines)
            {
                if (poly.IsFilled != drawFilledPass) continue;
                if (poly.Points.Count < 2 || !_visibleLayers.Contains(poly.LayerName)) continue;

                bool anyVisible = false;
                var screenPts = new Point[poly.Points.Count];
                for (int i = 0; i < poly.Points.Count; i++)
                {
                    Point sp = WorldToScreen(poly.Points[i].X, poly.Points[i].Y);
                    screenPts[i] = sp;
                    if (sp.X >= -200 && sp.X <= w + 200 && sp.Y >= -200 && sp.Y <= h + 200)
                        anyVisible = true;
                }
                if (!anyVisible && poly.Points.Count == 2) continue;

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(screenPts[0], isFilled: poly.IsFilled, isClosed: poly.IsClosed || poly.IsFilled);
                    ctx.PolyLineTo(screenPts.Skip(1).ToArray(), !poly.IsFilled, false);
                }
                geo.Freeze();

                if (poly.IsFilled)
                    dc.DrawGeometry(GetFillBrush(poly.Color, poly.IsDefaultWhite), null, geo);
                else if (poly.WorldWidth > 0)
                {
                    double pxWidth = Math.Clamp(poly.WorldWidth * _scale, 1.0, 80.0);
                    Color c = ResolveThemeColor(poly.Color, poly.IsDefaultWhite);
                    var widePen = new Pen(new SolidColorBrush(Color.FromArgb(220, c.R, c.G, c.B)), pxWidth)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round
                    };
                    widePen.Freeze();
                    dc.DrawGeometry(null, widePen, geo);
                }
                else
                    dc.DrawGeometry(null, GetPen(poly.Color, poly.IsDefaultWhite), geo);
            }
        }

        foreach (var circle in _cadModel.Circles)
        {
            if (!_visibleLayers.Contains(circle.LayerName)) continue;
            Point c = WorldToScreen(circle.CenterX, circle.CenterY);
            double r = circle.Radius * _scale;
            if (c.X + r < 0 || c.X - r > w || c.Y + r < 0 || c.Y - r > h) continue;
            dc.DrawEllipse(null, GetPen(circle.Color, circle.IsDefaultWhite), c, r, r);
        }

        foreach (var txt in _cadModel.Texts)
        {
            if (!_visibleLayers.Contains(txt.LayerName) || string.IsNullOrWhiteSpace(txt.CleanedText)) continue;
            double pxHeight = txt.Height * _scale;
            if (pxHeight < 5.0) continue; // جلوگیری از شلوغی متون خیلی ریز هنگام زوم‌آوت
            Point p = WorldToScreen(txt.X, txt.Y);
            if (p.X < -200 || p.X > w + 200 || p.Y < -100 || p.Y > h + 100) continue;

            Color c = ResolveThemeColor(txt.Color, txt.IsDefaultWhite);
            var ft = CreateFormattedText(txt.CleanedText, Math.Clamp(pxHeight, 7.0, 48.0), c, FontWeights.Normal);

            if (Math.Abs(txt.RotationDegrees) > 0.5)
            {
                dc.PushTransform(new RotateTransform(-txt.RotationDegrees, p.X, p.Y));
                dc.DrawText(ft, new Point(p.X, p.Y - ft.Height));
                dc.Pop();
            }
            else
            {
                dc.DrawText(ft, new Point(p.X, p.Y - ft.Height));
            }
        }
    }

    private void RenderSurveyPolygonAndPoints(DrawingContext dc)
    {
        if (_points.Count == 0) return;

        var screenPts = _points.Select(p => WorldToScreen(p.Easting, p.Northing)).ToList();

        // 1. چندضلعی محدوده (اگر 3 نقطه یا بیشتر باشد)
        if (screenPts.Count >= 2)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(screenPts[0], isFilled: screenPts.Count >= 3, isClosed: screenPts.Count >= 3);
                ctx.PolyLineTo(screenPts.Skip(1).ToList(), true, true);
            }
            geo.Freeze();

            Brush? fill = screenPts.Count >= 3
                ? new SolidColorBrush(_metrics.HasSelfIntersection
                    ? Color.FromArgb(55, 239, 68, 68)
                    : Color.FromArgb(48, 14, 165, 233))
                : null;
            var strokeColor = _metrics.HasSelfIntersection
                ? Color.FromRgb(239, 68, 68)
                : Color.FromRgb(56, 189, 248);
            var pen = new Pen(new SolidColorBrush(strokeColor), 2.5);
            pen.Freeze();

            dc.DrawGeometry(fill, pen, geo);
        }

        // 2. برچسب طول اضلاع روی وسط هر ضلع
        if (ShowEdgeDistances && _metrics.Edges.Count > 0)
        {
            foreach (var edge in _metrics.Edges)
            {
                Point mid = WorldToScreen(edge.MidX, edge.MidY);
                double len = UseGroundMeasurements ? edge.GroundLengthMeters : edge.PlanarLengthMeters;
                string label = $"{PersianTextHelper.FormatNumber(len, 2, UsePersianDigits)} m";
                DrawBadge(dc, mid, label, Color.FromArgb(225, 15, 23, 42), Color.FromRgb(250, 204, 21), 11.5);
            }
        }

        // 3. زوایای داخلی هر گوشه
        if (ShowInteriorAngles && _metrics.CornerAngles.Count > 0)
        {
            foreach (var ca in _metrics.CornerAngles)
            {
                Point p = WorldToScreen(ca.X, ca.Y);
                string angTxt = $"{PersianTextHelper.FormatPlainNumber(ca.InteriorAngleDegrees, 1, UsePersianDigits)}°";
                DrawBadge(dc, new Point(p.X, p.Y + 20), angTxt, Color.FromArgb(190, 30, 41, 59), Color.FromRgb(167, 243, 208), 10);
            }
        }

        // 4. برچسب مساحت در مرکز محدوده
        if (_metrics.IsPolygonClosed)
        {
            Point center = WorldToScreen(_metrics.CentroidX, _metrics.CentroidY);
            double area = UseGroundMeasurements ? _metrics.GroundAreaSqMeters : _metrics.PlanarAreaSqMeters;
            string areaText = $"مساحت: {PersianTextHelper.FormatNumber(area, 2, UsePersianDigits)} m²";
            DrawBadge(dc, center, areaText, Color.FromArgb(235, 2, 132, 199), Colors.White, 13, FontWeights.Bold);
        }

        // 5. نشانگر نقاط گوشه‌ها (A, B, C...)
        for (int i = 0; i < _points.Count; i++)
        {
            Point sp = screenPts[i];
            bool isSelected = i == SelectedPointIndex;
            double r = isSelected ? 7.5 : 5.5;

            var markerFill = isSelected ? Brushes.OrangeRed : new SolidColorBrush(Color.FromRgb(244, 63, 94));
            var markerPen = new Pen(Brushes.White, 2.0);
            dc.DrawEllipse(markerFill, markerPen, sp, r, r);

            DrawBadge(dc, new Point(sp.X + 18, sp.Y - 18), _points[i].Label,
                isSelected ? Color.FromRgb(234, 88, 12) : Color.FromRgb(30, 41, 59),
                Colors.White, 12.5, FontWeights.Bold);
        }
    }

    private void RenderMeasurementOverlay(DrawingContext dc)
    {
        if (_measurePoints.Count == 0) return;

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(34, 197, 94)), 2.0)
        {
            DashStyle = DashStyles.Dash
        };

        var screenPts = _measurePoints.Select(p => WorldToScreen(p.X, p.Y)).ToList();
        for (int i = 0; i < screenPts.Count - 1; i++)
            dc.DrawLine(pen, screenPts[i], screenPts[i + 1]);

        if (_hasMousePosition &&
            ((ToolMode == CanvasToolMode.Ruler && _measurePoints.Count == 1) ||
             (ToolMode == CanvasToolMode.AngleMeasure && _measurePoints.Count is 1 or 2)))
        {
            Point liveEnd = _activeSnap.HasValue ? WorldToScreen(_activeSnap.Value.X, _activeSnap.Value.Y) : _currentMouseScreen;
            dc.DrawLine(pen, screenPts[^1], liveEnd);
        }

        foreach (var sp in screenPts)
            dc.DrawEllipse(Brushes.LimeGreen, new Pen(Brushes.White, 1.5), sp, 4.5, 4.5);
    }

    private void RenderSnapIndicator(DrawingContext dc, SnapCandidate snap)
    {
        Point sp = WorldToScreen(snap.X, snap.Y);
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(250, 204, 21)), 2.0);

        switch (snap.Kind)
        {
            case SnapPointKind.Center:
                dc.DrawEllipse(null, pen, sp, 7, 7);
                break;
            case SnapPointKind.Midpoint:
                var tri = new StreamGeometry();
                using (var ctx = tri.Open())
                {
                    ctx.BeginFigure(new Point(sp.X, sp.Y - 7), false, true);
                    ctx.LineTo(new Point(sp.X - 7, sp.Y + 6), true, false);
                    ctx.LineTo(new Point(sp.X + 7, sp.Y + 6), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(null, pen, tri);
                break;
            default:
                dc.DrawRectangle(null, pen, new Rect(sp.X - 6, sp.Y - 6, 12, 12));
                break;
        }
    }

    private void DrawBadge(DrawingContext dc, Point center, string text, Color bg, Color fg, double fontSize, FontWeight? weight = null)
    {
        var ft = CreateFormattedText(text, fontSize, fg, weight ?? FontWeights.SemiBold);
        double padX = 6, padY = 2.5;
        var rect = new Rect(center.X - ft.Width * 0.5 - padX, center.Y - ft.Height * 0.5 - padY,
                            ft.Width + padX * 2, ft.Height + padY * 2);
        var bgBrush = new SolidColorBrush(bg);
        dc.DrawRoundedRectangle(bgBrush, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 0.8), rect, 4, 4);
        dc.DrawText(ft, new Point(rect.X + padX, rect.Y + padY));
    }

    private FormattedText CreateFormattedText(string text, double fontSize, Color color, FontWeight weight)
    {
        var typeface = new Typeface(App.PersianFontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        var brush = new SolidColorBrush(color);
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private Color ResolveThemeColor(RgbColor rgb, bool isDefaultWhite)
    {
        if (isDefaultWhite)
            return IsDarkBackground ? Color.FromRgb(230, 236, 245) : Color.FromRgb(25, 32, 44);

        if (!IsDarkBackground && rgb.R > 220 && rgb.G > 220 && rgb.B > 220)
            return Color.FromRgb(45, 55, 72);

        return Color.FromRgb(rgb.R, rgb.G, rgb.B);
    }
}
