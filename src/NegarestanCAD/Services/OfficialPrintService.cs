using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NegarestanCAD.Models;

namespace NegarestanCAD.Services;

/// <summary>
/// موتور ساخت و چاپ سند رسمی مهندسی (A4 / A3، عمودی / افقی) شامل نقشه برداری، لایه زیرکار DWG، کارتوش، جداول و کادر مهر و امضا
/// </summary>
public static class OfficialPrintService
{
    private const double DipPerMm = 96.0 / 25.4;

    public static Size GetPageSizeDips(PrintSettings settings)
    {
        double wMm = settings.PaperSize == PaperSizeOption.A3 ? 297.0 : 210.0;
        double hMm = settings.PaperSize == PaperSizeOption.A3 ? 420.0 : 297.0;

        if (settings.Orientation == PaperOrientationOption.Landscape)
            (wMm, hMm) = (hMm, wMm);

        return new Size(wMm * DipPerMm, hMm * DipPerMm);
    }

    /// <summary>ساخت صفحه کامل رسمی برای پیش‌نمایش زنده و چاپ برداری</summary>
    public static Grid BuildOfficialSheetVisual(
        CadRenderModel? cadModel,
        IReadOnlyList<SurveyPoint> points,
        PolygonMetrics metrics,
        PrintSettings settings,
        bool persianDigits)
    {
        Size pageSize = GetPageSizeDips(settings);
        double margin = 28; // حدود 7.4mm

        var root = new Grid
        {
            Width = pageSize.Width,
            Height = pageSize.Height,
            Background = Brushes.White,
            FlowDirection = FlowDirection.RightToLeft,
            //FontFamily = App.PersianFontFamily
        };

        // کادر مهندسی دوتایی دور صفحه
        var outerFrame = new Border
        {
            Margin = new Thickness(margin),
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            BorderThickness = new Thickness(2.0),
            Padding = new Thickness(3)
        };

        var innerFrame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(10)
        };

        var mainLayout = new Grid();
        mainLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // هدر و کارتوش بالا
        mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // نقشه + جداول
        mainLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // خلاصه مساحت و کادر مهر و امضا

        // 1. نوار عنوان و کارتوش بالا
        var headerBlock = BuildHeaderCartouche(settings, points, metrics, persianDigits);
        Grid.SetRow(headerBlock, 0);
        mainLayout.Children.Add(headerBlock);

        // 2. بخش میانی: نقشه + پنل جداول کنار (در حالت افقی) یا زیر (در حالت عمودی)
        var bodyGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        bool isLandscape = settings.Orientation == PaperOrientationOption.Landscape;

        if (isLandscape)
        {
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.55, GridUnitType.Star) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        }
        else
        {
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.35, GridUnitType.Star) });
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
        }

        var mapVisual = new PrintMapElement(cadModel, points, metrics, settings, persianDigits);
        var mapBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1.0),
            Background = Brushes.White,
            Child = mapVisual
        };

        var tablesPanel = BuildTablesPanel(points, metrics, settings, persianDigits);

        if (isLandscape)
        {
            Grid.SetColumn(mapBorder, 0);
            Grid.SetColumn(tablesPanel, 2);
        }
        else
        {
            Grid.SetRow(mapBorder, 0);
            Grid.SetRow(tablesPanel, 2);
        }

        bodyGrid.Children.Add(mapBorder);
        bodyGrid.Children.Add(tablesPanel);
        Grid.SetRow(bodyGrid, 1);
        mainLayout.Children.Add(bodyGrid);

        // 3. فوتر رسمی: خلاصه متراژ، توضیحات و محل مهر و امضا
        var footer = BuildFooterBlock(settings, metrics, persianDigits);
        Grid.SetRow(footer, 2);
        mainLayout.Children.Add(footer);

        innerFrame.Child = mainLayout;
        outerFrame.Child = innerFrame;
        root.Children.Add(outerFrame);

        root.Measure(pageSize);
        root.Arrange(new Rect(new Point(0, 0), pageSize));
        root.UpdateLayout();

        return root;
    }

    /// <summary>ارسال مستقیم سند رسمی به پنجره چاپ ویندوز (پرینتر یا Microsoft Print to PDF)</summary>
    public static bool PrintSheet(
        CadRenderModel? cadModel,
        IReadOnlyList<SurveyPoint> points,
        PolygonMetrics metrics,
        PrintSettings settings,
        bool persianDigits)
    {
        var dlg = new PrintDialog();
        dlg.PrintTicket.PageOrientation = settings.Orientation == PaperOrientationOption.Landscape
            ? PageOrientation.Landscape
            : PageOrientation.Portrait;
        dlg.PrintTicket.PageMediaSize = settings.PaperSize == PaperSizeOption.A3
            ? new PageMediaSize(PageMediaSizeName.ISOA3)
            : new PageMediaSize(PageMediaSizeName.ISOA4);

        if (dlg.ShowDialog() != true)
            return false;

        Size targetSize = GetPageSizeDips(settings);
        Grid sheet = BuildOfficialSheetVisual(cadModel, points, metrics, settings, persianDigits);

        var fixedDoc = new FixedDocument();
        fixedDoc.DocumentPaginator.PageSize = targetSize;

        var page = new FixedPage { Width = targetSize.Width, Height = targetSize.Height };
        page.Children.Add(sheet);
        page.Measure(targetSize);
        page.Arrange(new Rect(new Point(0, 0), targetSize));
        page.UpdateLayout();

        var pageContent = new PageContent();
        ((IAddChild)pageContent).AddChild(page);
        fixedDoc.Pages.Add(pageContent);

        dlg.PrintDocument(fixedDoc.DocumentPaginator, settings.ProjectTitle);
        return true;
    }

    /// <summary>خروجی تصویر باکیفیت 300 DPI از برگه چاپ رسمی (PNG)</summary>
    public static void ExportSheetToPng(
        string filePath,
        CadRenderModel? cadModel,
        IReadOnlyList<SurveyPoint> points,
        PolygonMetrics metrics,
        PrintSettings settings,
        bool persianDigits)
    {
        Size size = GetPageSizeDips(settings);
        Grid sheet = BuildOfficialSheetVisual(cadModel, points, metrics, settings, persianDigits);

        const double dpi = 300.0;
        double scale = dpi / 96.0;
        int pxW = (int)Math.Ceiling(size.Width * scale);
        int pxH = (int)Math.Ceiling(size.Height * scale);

        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(sheet);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    private static UIElement BuildHeaderCartouche(PrintSettings settings, IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, bool persianDigits)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(30, 58, 95)),
            BorderThickness = new Thickness(1.2),
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            Padding = new Thickness(10, 6, 10, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // لوگو (در صورت آپلود)
        if (!string.IsNullOrWhiteSpace(settings.LogoPath) && File.Exists(settings.LogoPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(settings.LogoPath, UriKind.Absolute);
                bmp.EndInit();

                var img = new Image
                {
                    Source = bmp,
                    MaxHeight = 52,
                    MaxWidth = 90,
                    Margin = new Thickness(0, 0, 12, 0),
                    Stretch = Stretch.Uniform
                };
                Grid.SetColumn(img, 0);
                grid.Children.Add(img);
            }
            catch { }
        }

        // عنوان و مشخصات مالک/پلاک
        var centerStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        centerStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(settings.ProjectTitle) ? "نقشه تفکیکی و تعیین محدوده UTM" : settings.ProjectTitle,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
        });

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.OwnerName)) metaParts.Add($"مالک / متقاضی: {settings.OwnerName}");
        if (!string.IsNullOrWhiteSpace(settings.ParcelNumber)) metaParts.Add($"پلاک ثبتی: {settings.ParcelNumber}");
        if (!string.IsNullOrWhiteSpace(settings.LocationAddress)) metaParts.Add($"نشانی: {settings.LocationAddress}");

        if (metaParts.Count > 0)
        {
            centerStack.Children.Add(new TextBlock
            {
                Text = string.Join("   |   ", metaParts),
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
            });
        }
        Grid.SetColumn(centerStack, 1);
        grid.Children.Add(centerStack);

        // اطلاعات فنی سمت چپ کارتوش
        string zoneStr = points.Count > 0 ? points[0].ZoneDisplay : "41N";
        string dateStr = string.IsNullOrWhiteSpace(settings.CustomDateShamsi)
            ? PersianTextHelper.GetTodayShamsi(persianDigits)
            : settings.CustomDateShamsi;

        var rightStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        rightStack.Children.Add(new TextBlock { Text = $"تاریخ: {dateStr}", FontSize = 10, FontWeight = FontWeights.SemiBold });
        rightStack.Children.Add(new TextBlock { Text = $"سیستم مختصات: WGS84 / UTM {zoneStr}", FontSize = 9.5, Foreground = Brushes.DimGray });
        if (!string.IsNullOrWhiteSpace(settings.ScaleLabelOverride))
            rightStack.Children.Add(new TextBlock { Text = $"مقیاس: {settings.ScaleLabelOverride}", FontSize = 9.5, FontWeight = FontWeights.SemiBold });

        Grid.SetColumn(rightStack, 2);
        grid.Children.Add(rightStack);

        border.Child = grid;
        return border;
    }

    private static UIElement BuildTablesPanel(IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, PrintSettings settings, bool persianDigits)
    {
        var stack = new StackPanel();

        if (settings.ShowCoordinatesTable)
        {
            stack.Children.Add(BuildSectionHeader("جدول مختصات گوشه‌ها (UTM و جغرافیایی)"));
            var coordGrid = BuildTableGrid(new[] { "نقطه", "X (Easting)", "Y (Northing)", "عرض (Lat)", "طول (Lon)" });

            int maxRows = Math.Min(points.Count, 28);
            for (int i = 0; i < maxRows; i++)
            {
                var p = points[i];
                AddTableRow(coordGrid, i, new[]
                {
                    p.Label,
                    PersianTextHelper.FormatPlainNumber(p.Easting, 3, persianDigits),
                    PersianTextHelper.FormatPlainNumber(p.Northing, 3, persianDigits),
                    PersianTextHelper.FormatPlainNumber(p.Latitude, 6, persianDigits),
                    PersianTextHelper.FormatPlainNumber(p.Longitude, 6, persianDigits)
                });
            }
            stack.Children.Add(coordGrid);
        }

        if (settings.ShowEdgesTable && metrics.Edges.Count > 0)
        {
            var spacer = new Border { Height = 8 };
            stack.Children.Add(spacer);
            stack.Children.Add(BuildSectionHeader("جدول طول اضلاع و آزیموت"));

            var edgeGrid = BuildTableGrid(new[] { "ضلع", "طول (متر)", "آزیموت" });
            int maxEdges = Math.Min(metrics.Edges.Count, 28);
            for (int i = 0; i < maxEdges; i++)
            {
                var e = metrics.Edges[i];
                double len = settings.UseGroundArea ? e.GroundLengthMeters : e.PlanarLengthMeters;
                AddTableRow(edgeGrid, i, new[]
                {
                    e.EdgeName,
                    PersianTextHelper.FormatNumber(len, 2, persianDigits),
                    $"{PersianTextHelper.FormatPlainNumber(e.AzimuthDegrees, 1, persianDigits)}°"
                });
            }
            stack.Children.Add(edgeGrid);
        }

        return stack;
    }

    private static UIElement BuildFooterBlock(PrintSettings settings, PolygonMetrics metrics, bool persianDigits)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

        // خلاصه مساحت و محیط
        double area = settings.UseGroundArea ? metrics.GroundAreaSqMeters : metrics.PlanarAreaSqMeters;
        double hectares = area / 10000.0;
        double perim = settings.UseGroundArea ? metrics.GroundPerimeterMeters : metrics.PlanarPerimeterMeters;
        string areaKind = settings.UseGroundArea ? "واقعی روی زمین (با ضریب مقیاس UTM)" : "مسطح کدی (Planar UTM)";

        var summaryBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(30, 58, 95)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            Padding = new Thickness(10, 6, 10, 6)
        };

        var sumStack = new StackPanel();
        sumStack.Children.Add(new TextBlock
        {
            Text = $"مساحت کل ({areaKind}):  {PersianTextHelper.FormatNumber(area, 2, persianDigits)} مترمربع   ({PersianTextHelper.FormatPlainNumber(hectares, 4, persianDigits)} هکتار)",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
        });
        sumStack.Children.Add(new TextBlock
        {
            Text = $"محیط کل: {PersianTextHelper.FormatNumber(perim, 2, persianDigits)} متر   |   ضریب مقیاس UTM: {PersianTextHelper.FormatPlainNumber(metrics.AverageScaleFactor, 6, persianDigits)}   |   مساحت مسطح UTM: {PersianTextHelper.FormatNumber(metrics.PlanarAreaSqMeters, 2, persianDigits)} مترمربع",
            FontSize = 9.5,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
        });
        if (!string.IsNullOrWhiteSpace(settings.Notes))
        {
            sumStack.Children.Add(new TextBlock
            {
                Text = $"توضیحات: {settings.Notes}",
                FontSize = 9,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            });
        }
        summaryBorder.Child = sumStack;
        Grid.SetColumn(summaryBorder, 0);
        grid.Children.Add(summaryBorder);

        // کادر مهندس و محل مهر و امضا
        var sigBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            MinHeight = 58
        };
        var sigStack = new StackPanel();
        string engTitle = string.IsNullOrWhiteSpace(settings.EngineerOrCompany) ? "مهندس / شرکت نقشه‌برداری" : settings.EngineerOrCompany;
        sigStack.Children.Add(new TextBlock { Text = engTitle, FontSize = 10, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrWhiteSpace(settings.EngineerLicenseNumber))
            sigStack.Children.Add(new TextBlock { Text = $"شماره پروانه / نظام مهندسی: {settings.EngineerLicenseNumber}", FontSize = 8.5, Foreground = Brushes.DimGray });
        sigStack.Children.Add(new TextBlock
        {
            Text = "محل مهر و امضا:",
            FontSize = 9,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = Brushes.Gray
        });
        sigBorder.Child = sigStack;
        Grid.SetColumn(sigBorder, 2);
        grid.Children.Add(sigBorder);

        return grid;
    }

    private static UIElement BuildSectionHeader(string title) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 58, 95)),
        Padding = new Thickness(6, 3, 6, 3),
        Child = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold
        }
    };

    private static Grid BuildTableGrid(string[] headers)
    {
        var g = new Grid();
        for (int i = 0; i < headers.Length; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });

        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(0.5),
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Padding = new Thickness(4, 2, 4, 2),
                Child = new TextBlock
                {
                    Text = headers[i],
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            };
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, i);
            g.Children.Add(cell);
        }
        return g;
    }

    private static void AddTableRow(Grid g, int dataIndex, string[] values)
    {
        int rowIdx = dataIndex + 1;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bg = dataIndex % 2 == 1 ? new SolidColorBrush(Color.FromRgb(248, 250, 252)) : Brushes.White;

        for (int i = 0; i < values.Length; i++)
        {
            var cell = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(0.5),
                Background = bg,
                Padding = new Thickness(4, 1.5, 4, 1.5),
                Child = new TextBlock
                {
                    Text = values[i],
                    FontSize = 8.5,
                    FlowDirection = FlowDirection.LeftToRight,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal
                }
            };
            Grid.SetRow(cell, rowIdx);
            Grid.SetColumn(cell, i);
            g.Children.Add(cell);
        }
    }

    /// <summary>المان برداری نقشه برای چاپ (شامل زیرکار DWG، محدوده، فواصل، فلش شمال و خط‌کش مقیاس گرافیکی)</summary>
    private sealed class PrintMapElement : FrameworkElement
    {
        private readonly CadRenderModel? _cad;
        private readonly IReadOnlyList<SurveyPoint> _points;
        private readonly PolygonMetrics _metrics;
        private readonly PrintSettings _settings;
        private readonly bool _persianDigits;

        public PrintMapElement(CadRenderModel? cad, IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, PrintSettings settings, bool persianDigits)
        {
            FlowDirection = FlowDirection.LeftToRight;
            _cad = cad;
            _points = points;
            _metrics = metrics;
            _settings = settings;
            _persianDigits = persianDigits;
            ClipToBounds = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w <= 10 || h <= 10) return;

            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));

            // تعیین محدوده دید (تمرکز روی محدوده نقاط؛ اگر نقطه نبود کل نقشه DWG)
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

            if (_points.Count > 0)
            {
                minX = _points.Min(p => p.Easting);
                minY = _points.Min(p => p.Northing);
                maxX = _points.Max(p => p.Easting);
                maxY = _points.Max(p => p.Northing);

                double dx = Math.Max(10.0, maxX - minX);
                double dy = Math.Max(10.0, maxY - minY);
                minX -= dx * 0.18; maxX += dx * 0.18;
                minY -= dy * 0.18; maxY += dy * 0.18;
            }
            else if (_cad != null && _cad.HasGeometry)
            {
                minX = _cad.MinX; minY = _cad.MinY;
                maxX = _cad.MaxX; maxY = _cad.MaxY;
            }
            else
            {
                return;
            }

            double cx = (minX + maxX) * 0.5;
            double cy = (minY + maxY) * 0.5;
            double scale = Math.Min((w * 0.86) / Math.Max(1.0, maxX - minX), (h * 0.82) / Math.Max(1.0, maxY - minY));

            Point W2S(double wx, double wy) => new((wx - cx) * scale + w * 0.5, -(wy - cy) * scale + h * 0.48);

            // 1. لایه زیرکار DWG
            if (_settings.IncludeDwgUnderlay && _cad != null)
            {
                var visibleLayers = _cad.Layers.Where(l => l.IsVisible).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var grayPen = new Pen(new SolidColorBrush(Color.FromRgb(185, 195, 208)), 0.65);
                grayPen.Freeze();

                foreach (var hatch in _cad.Hatches)
                {
                    if (!visibleLayers.Contains(hatch.LayerName) || hatch.Loops.Count == 0) continue;
                    var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
                    bool hasLoop = false;
                    using (var ctx = geo.Open())
                    {
                        foreach (var loop in hatch.Loops)
                        {
                            if (loop.Count < 3) continue;
                            var sPts = loop.Select(p => W2S(p.X, p.Y)).ToArray();
                            ctx.BeginFigure(sPts[0], true, true);
                            ctx.PolyLineTo(sPts.Skip(1).ToArray(), false, false);
                            hasLoop = true;
                        }
                    }
                    if (!hasLoop) continue;
                    geo.Freeze();
                    Color hc = _settings.GrayscaleDwgUnderlay
                        ? Color.FromArgb(hatch.IsSolid ? (byte)45 : (byte)22, 148, 163, 184)
                        : Color.FromArgb(hatch.IsSolid ? (byte)150 : (byte)55, hatch.Color.R, hatch.Color.G, hatch.Color.B);
                    dc.DrawGeometry(new SolidColorBrush(hc), null, geo);
                }

                foreach (var poly in _cad.Polylines)
                {
                    if (poly.Points.Count < 2 || !visibleLayers.Contains(poly.LayerName)) continue;
                    var pts = poly.Points.Select(p => W2S(p.X, p.Y)).ToArray();
                    if (pts.All(p => p.X < -50 || p.X > w + 50 || p.Y < -50 || p.Y > h + 50)) continue;

                    Pen pen = _settings.GrayscaleDwgUnderlay
                        ? grayPen
                        : new Pen(new SolidColorBrush(poly.IsDefaultWhite ? Color.FromRgb(60, 70, 85) : Color.FromRgb(poly.Color.R, poly.Color.G, poly.Color.B)), 0.7);

                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(pts[0], false, poly.IsClosed);
                        ctx.PolyLineTo(pts.Skip(1).ToArray(), true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, pen, geo);
                }

                foreach (var c in _cad.Circles)
                {
                    if (!visibleLayers.Contains(c.LayerName)) continue;
                    Point cp = W2S(c.CenterX, c.CenterY);
                    double r = c.Radius * scale;
                    if (cp.X + r < 0 || cp.X - r > w || cp.Y + r < 0 || cp.Y - r > h) continue;
                    dc.DrawEllipse(null, grayPen, cp, r, r);
                }
            }

            // 2. محدوده نقاط UTM
            if (_points.Count >= 2)
            {
                var sPts = _points.Select(p => W2S(p.Easting, p.Northing)).ToList();
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(sPts[0], sPts.Count >= 3, sPts.Count >= 3);
                    ctx.PolyLineTo(sPts.Skip(1).ToList(), true, true);
                }
                geo.Freeze();

                var fill = sPts.Count >= 3 ? new SolidColorBrush(Color.FromArgb(40, 2, 132, 199)) : null;
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(185, 28, 28)), 2.0);
                dc.DrawGeometry(fill, pen, geo);

                // طول اضلاع
                if (_settings.ShowEdgeDistances)
                {
                    foreach (var edge in _metrics.Edges)
                    {
                        Point mp = W2S(edge.MidX, edge.MidY);
                        double len = _settings.UseGroundArea ? edge.GroundLengthMeters : edge.PlanarLengthMeters;
                        DrawPrintLabel(dc, mp, $"{PersianTextHelper.FormatNumber(len, 2, _persianDigits)} m", Colors.White, Color.FromRgb(15, 23, 42), 9);
                    }
                }

                // گوشه‌ها
                for (int i = 0; i < _points.Count; i++)
                {
                    Point sp = sPts[i];
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(185, 28, 28)), new Pen(Brushes.White, 1.2), sp, 3.8, 3.8);
                    DrawPrintLabel(dc, new Point(sp.X + 12, sp.Y - 11), _points[i].Label, Color.FromRgb(15, 23, 42), Colors.White, 9.5, FontWeights.Bold);
                }

                // مساحت در مرکز
                if (_metrics.IsPolygonClosed)
                {
                    Point center = W2S(_metrics.CentroidX, _metrics.CentroidY);
                    double area = _settings.UseGroundArea ? _metrics.GroundAreaSqMeters : _metrics.PlanarAreaSqMeters;
                    DrawPrintLabel(dc, center, $"S = {PersianTextHelper.FormatNumber(area, 2, _persianDigits)} m²", Color.FromRgb(224, 242, 254), Color.FromRgb(12, 74, 110), 10, FontWeights.Bold);
                }
            }

            // 3. فلش شمال (بالا-چپ کادر نقشه)
            DrawNorthArrow(dc, new Point(34, 42));

            // 4. خط‌کش مقیاس گرافیکی و مقیاس عددی (پایین-چپ کادر نقشه)
            DrawScaleBar(dc, new Point(22, h - 22), scale);
        }

        private void DrawNorthArrow(DrawingContext dc, Point center)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(15, 23, 42)), 1.3);
            var dark = new SolidColorBrush(Color.FromRgb(15, 23, 42));

            var leftTri = new StreamGeometry();
            using (var ctx = leftTri.Open())
            {
                ctx.BeginFigure(new Point(center.X, center.Y - 18), true, true);
                ctx.LineTo(new Point(center.X - 7, center.Y + 10), true, false);
                ctx.LineTo(new Point(center.X, center.Y + 4), true, false);
            }
            leftTri.Freeze();
            dc.DrawGeometry(dark, pen, leftTri);

            var rightTri = new StreamGeometry();
            using (var ctx = rightTri.Open())
            {
                ctx.BeginFigure(new Point(center.X, center.Y - 18), true, true);
                ctx.LineTo(new Point(center.X + 7, center.Y + 10), true, false);
                ctx.LineTo(new Point(center.X, center.Y + 4), true, false);
            }
            rightTri.Freeze();
            dc.DrawGeometry(Brushes.White, pen, rightTri);

            var ft = MakeText("N", 10, Color.FromRgb(15, 23, 42), FontWeights.Bold);
            dc.DrawText(ft, new Point(center.X - ft.Width * 0.5, center.Y - 32));
        }

        private void DrawScaleBar(DrawingContext dc, Point origin, double pxPerMeter)
        {
            // انتخاب طول رند و استاندارد برای خط‌کش مقیاس (مثلاً 10m, 20m, 50m, 100m)
            double targetMeters = 100.0 / pxPerMeter;
            double[] niceSteps = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 500, 1000, 2000, 5000 };
            double barMeters = niceSteps.FirstOrDefault(s => s >= targetMeters);
            if (barMeters <= 0) barMeters = Math.Round(targetMeters);

            double barPx = barMeters * pxPerMeter;
            double halfPx = barPx * 0.5;

            var pen = new Pen(Brushes.Black, 1.0);
            dc.DrawRectangle(Brushes.Black, pen, new Rect(origin.X, origin.Y - 6, halfPx, 6));
            dc.DrawRectangle(Brushes.White, pen, new Rect(origin.X + halfPx, origin.Y - 6, halfPx, 6));

            var t0 = MakeText("0", 8.5, Colors.Black, FontWeights.Normal);
            var tEnd = MakeText($"{barMeters:0.#} m", 8.5, Colors.Black, FontWeights.SemiBold);
            dc.DrawText(t0, new Point(origin.X - 3, origin.Y - 19));
            dc.DrawText(tEnd, new Point(origin.X + barPx - tEnd.Width * 0.5, origin.Y - 19));

            // مقیاس عددی تقریبی روی کاغذ (1 متر = 1000mm، و هر DIP = 25.4/96 mm)
            double paperMetersPerDip = (25.4 / 96.0) / 1000.0;
            double numericScale = Math.Round(1.0 / (pxPerMeter * paperMetersPerDip));
            var tScale = MakeText($"Scale ≈ 1 : {numericScale:N0}", 8.5, Color.FromRgb(51, 65, 85), FontWeights.SemiBold);
            dc.DrawText(tScale, new Point(origin.X, origin.Y + 3));
        }

        private void DrawPrintLabel(DrawingContext dc, Point center, string text, Color bg, Color fg, double fontSize, FontWeight? weight = null)
        {
            var ft = MakeText(text, fontSize, fg, weight ?? FontWeights.SemiBold);
            double px = 4, py = 1.5;
            var rect = new Rect(center.X - ft.Width * 0.5 - px, center.Y - ft.Height * 0.5 - py, ft.Width + px * 2, ft.Height + py * 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(Color.FromArgb(110, 100, 116, 139)), 0.6), rect, 3, 3);
            dc.DrawText(ft, new Point(rect.X + px, rect.Y + py));
        }

        private FormattedText MakeText(string text, double size, Color color, FontWeight weight)
        {
            var tf = new Typeface(App.PersianFontFamily, FontStyles.Normal, weight, FontStretches.Normal);
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size,
                new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }
}
