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
/// موتور ساخت و چاپ سند رسمی مهندسی (A4 / A3، عمودی / افقی) با پشتیبانی همزمان از نقشه CAD و نقشه ماهواره‌ای
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

    public static Grid BuildOfficialSheetVisual(
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
        Size pageSize = GetPageSizeDips(settings);
        double margin = 28;

        var root = new Grid
        {
            Width = pageSize.Width,
            Height = pageSize.Height,
            Background = Brushes.White,
            FlowDirection = FlowDirection.RightToLeft,
        };

        TextElement.SetFontFamily(root, App.PersianFontFamily);
        TextElement.SetFontFamily(root, App.PersianFontFamily);
        TextElement.SetForeground(root, new SolidColorBrush(Color.FromRgb(15, 23, 42)));

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
        mainLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1. نوار عنوان و کارتوش بالا
        var headerBlock = BuildHeaderCartouche(settings, points, metrics, persianDigits, activeZone, activeNorthern);
        Grid.SetRow(headerBlock, 0);
        mainLayout.Children.Add(headerBlock);

        // 2. بخش میانی: نقشه (CAD / ماهواره / هر دو کنار هم) + جداول
        var bodyGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        bool isLandscape = settings.Orientation == PaperOrientationOption.Landscape;

        if (isLandscape)
        {
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.65, GridUnitType.Star) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        }
        else
        {
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.38, GridUnitType.Star) });
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
        }

        UIElement mapArea = BuildMapsArea(cadModel, points, metrics, settings, persianDigits, activeZone, activeNorthern, fallbackLat, fallbackLon);
        var tablesPanel = BuildTablesPanel(points, metrics, settings, persianDigits);

        if (isLandscape)
        {
            Grid.SetColumn(mapArea, 0);
            Grid.SetColumn(tablesPanel, 2);
        }
        else
        {
            Grid.SetRow(mapArea, 0);
            Grid.SetRow(tablesPanel, 2);
        }

        bodyGrid.Children.Add(mapArea);
        bodyGrid.Children.Add(tablesPanel);
        Grid.SetRow(bodyGrid, 1);
        mainLayout.Children.Add(bodyGrid);

        // 3. فوتر رسمی
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

    private static UIElement BuildMapsArea(
        CadRenderModel? cadModel,
        IReadOnlyList<SurveyPoint> points,
        PolygonMetrics metrics,
        PrintSettings settings,
        bool persianDigits,
        int activeZone,
        bool activeNorthern,
        double fallbackLat,
        double fallbackLon)
    {
        // اگر فایل CAD باز نشده باشد و حالت Both انتخاب شده باشد، یا حالت SatelliteOnly باشد
        PrintMapMode effectiveMode = settings.MapMode;
        if (effectiveMode == PrintMapMode.BothSideBySide && (cadModel == null || !cadModel.HasGeometry) && !settings.IncludeDwgUnderlay)
            effectiveMode = PrintMapMode.SatelliteOnly;

        Border WrapMap(UIElement child, string titleBadge)
        {
            var container = new Grid();
            container.Children.Add(child);

            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(215, 15, 23, 42)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6),
                Child = new TextBlock
                {
                    Text = titleBadge,
                    Foreground = Brushes.White,
                    FontSize = 8.5,
                    FontWeight = FontWeights.SemiBold
                }
            };
            container.Children.Add(badge);

            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1.0),
                Background = Brushes.White,
                Child = container
            };
        }

        if (effectiveMode == PrintMapMode.CadOnly)
        {
            return WrapMap(new PrintCadMapElement(cadModel, points, metrics, settings, persianDigits), "نقشه کدی (CAD / UTM)");
        }

        if (effectiveMode == PrintMapMode.SatelliteOnly)
        {
            return WrapMap(
                new PrintSatelliteMapElement(points, metrics, settings, persianDigits, activeZone, activeNorthern, fallbackLat, fallbackLon),
                "تصویر ماهواره‌ای زمین");
        }

        // حالت دوگانه: نقشه CAD و نقشه ماهواره‌ای کنار هم
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cadBox = WrapMap(new PrintCadMapElement(cadModel, points, metrics, settings, persianDigits), "نقشه کدی (CAD / UTM)");
        var satBox = WrapMap(
            new PrintSatelliteMapElement(points, metrics, settings, persianDigits, activeZone, activeNorthern, fallbackLat, fallbackLon),
            "تصویر ماهواره‌ای زمین");

        Grid.SetColumn(cadBox, 0);
        Grid.SetColumn(satBox, 2);
        split.Children.Add(cadBox);
        split.Children.Add(satBox);
        return split;
    }

    public static bool PrintSheet(
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
        Grid sheet = BuildOfficialSheetVisual(cadModel, points, metrics, settings, persianDigits, activeZone, activeNorthern, fallbackLat, fallbackLon);

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

    public static void ExportSheetToPng(
        string filePath,
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
        Size size = GetPageSizeDips(settings);
        Grid sheet = BuildOfficialSheetVisual(cadModel, points, metrics, settings, persianDigits, activeZone, activeNorthern, fallbackLat, fallbackLon);

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

    private static UIElement BuildHeaderCartouche(PrintSettings settings, IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, bool persianDigits, int activeZone, bool activeNorthern)
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

        string zoneStr = points.Count > 0 ? points[0].ZoneDisplay : $"{activeZone}{(activeNorthern ? "N" : "S")}";
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
            stack.Children.Add(new Border { Height = 8 });
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

        var sigBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            MinHeight = 58
        };
        var sigStack = new StackPanel();
        string engTitle = string.IsNullOrWhiteSpace(settings.EngineerOrCompany) ? "شهرداری بریس" : settings.EngineerOrCompany;
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

    private static void DrawNorthArrowShared(DrawingContext dc, Visual visual, Point center, bool lightBg)
    {
        Color primary = lightBg ? Color.FromRgb(15, 23, 42) : Colors.White;
        var pen = new Pen(new SolidColorBrush(primary), 1.3);
        var fillDark = new SolidColorBrush(primary);

        var leftTri = new StreamGeometry();
        using (var ctx = leftTri.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - 16), true, true);
            ctx.LineTo(new Point(center.X - 6, center.Y + 9), true, false);
            ctx.LineTo(new Point(center.X, center.Y + 4), true, false);
        }
        leftTri.Freeze();
        dc.DrawGeometry(fillDark, pen, leftTri);

        var rightTri = new StreamGeometry();
        using (var ctx = rightTri.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - 16), true, true);
            ctx.LineTo(new Point(center.X + 6, center.Y + 9), true, false);
            ctx.LineTo(new Point(center.X, center.Y + 4), true, false);
        }
        rightTri.Freeze();
        dc.DrawGeometry(lightBg ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 41, 59)), pen, rightTri);

        var ft = MakeSharedText(visual, "N", 9.5, primary, FontWeights.Bold);
        dc.DrawText(ft, new Point(center.X - ft.Width * 0.5, center.Y - 29));
    }

    private static void DrawScaleBarShared(DrawingContext dc, Visual visual, Point origin, double pxPerMeter, bool lightBg)
    {
        double targetMeters = 85.0 / Math.Max(1e-6, pxPerMeter);
        double[] niceSteps = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 500, 1000, 2000, 5000 };
        double barMeters = niceSteps.FirstOrDefault(s => s >= targetMeters);
        if (barMeters <= 0) barMeters = Math.Round(targetMeters);

        double barPx = Math.Clamp(barMeters * pxPerMeter, 28, 140);
        double halfPx = barPx * 0.5;

        if (!lightBg)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(185, 15, 23, 42)), null,
                new Rect(origin.X - 6, origin.Y - 20, Math.Max(barPx + 24, 105), 34), 4, 4);
        }

        Color fg = lightBg ? Colors.Black : Colors.White;
        var pen = new Pen(new SolidColorBrush(fg), 0.9);
        dc.DrawRectangle(new SolidColorBrush(fg), pen, new Rect(origin.X, origin.Y - 5, halfPx, 5));
        dc.DrawRectangle(lightBg ? Brushes.White : Brushes.DimGray, pen, new Rect(origin.X + halfPx, origin.Y - 5, halfPx, 5));

        var t0 = MakeSharedText(visual, "0", 8, fg, FontWeights.Normal);
        var tEnd = MakeSharedText(visual, $"{barMeters:0.#} m", 8, fg, FontWeights.SemiBold);
        dc.DrawText(t0, new Point(origin.X - 2, origin.Y - 17));
        dc.DrawText(tEnd, new Point(origin.X + barPx - tEnd.Width * 0.5, origin.Y - 17));

        double paperMetersPerDip = (25.4 / 96.0) / 1000.0;
        double numericScale = Math.Round(1.0 / (pxPerMeter * paperMetersPerDip));
        var tScale = MakeSharedText(visual, $"Scale ≈ 1 : {numericScale:N0}", 8, fg, FontWeights.SemiBold);
        dc.DrawText(tScale, new Point(origin.X, origin.Y + 2));
    }

    private static void DrawPrintLabelShared(DrawingContext dc, Visual visual, Point center, string text, Color bg, Color fg, double fontSize, FontWeight? weight = null)
    {
        var ft = MakeSharedText(visual, text, fontSize, fg, weight ?? FontWeights.SemiBold);
        double px = 4, py = 1.5;
        var rect = new Rect(center.X - ft.Width * 0.5 - px, center.Y - ft.Height * 0.5 - py, ft.Width + px * 2, ft.Height + py * 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(Color.FromArgb(110, 100, 116, 139)), 0.6), rect, 3, 3);
        dc.DrawText(ft, new Point(rect.X + px, rect.Y + py));
    }

    private static FormattedText MakeSharedText(Visual visual, string text, double size, Color color, FontWeight weight)
    {
        var tf = new Typeface(App.PersianFontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size,
            new SolidColorBrush(color), VisualTreeHelper.GetDpi(visual).PixelsPerDip);
    }

    /// <summary>المان برداری نقشه CAD برای چاپ رسمی</summary>
    private sealed class PrintCadMapElement : FrameworkElement
    {
        private readonly CadRenderModel? _cad;
        private readonly IReadOnlyList<SurveyPoint> _points;
        private readonly PolygonMetrics _metrics;
        private readonly PrintSettings _settings;
        private readonly bool _persianDigits;

        public PrintCadMapElement(CadRenderModel? cad, IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, PrintSettings settings, bool persianDigits)
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

            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

            if (_points.Count > 0)
            {
                minX = _points.Min(p => p.Easting);
                minY = _points.Min(p => p.Northing);
                maxX = _points.Max(p => p.Easting);
                maxY = _points.Max(p => p.Northing);

                double dx = Math.Max(15.0, maxX - minX);
                double dy = Math.Max(15.0, maxY - minY);
                minX -= dx * 0.22; maxX += dx * 0.22;
                minY -= dy * 0.22; maxY += dy * 0.22;
            }
            else if (_cad != null && _cad.HasGeometry)
            {
                minX = _cad.MinX; minY = _cad.MinY;
                maxX = _cad.MaxX; maxY = _cad.MaxY;
            }
            else
            {
                var emptyFt = MakeSharedText(this, "فایل CAD انتخاب نشده است", 11, Colors.Gray, FontWeights.Normal);
                dc.DrawText(emptyFt, new Point((w - emptyFt.Width) * 0.5, (h - emptyFt.Height) * 0.5));
                return;
            }

            double cx = (minX + maxX) * 0.5;
            double cy = (minY + maxY) * 0.5;
            double scale = Math.Min((w * 0.86) / Math.Max(1.0, maxX - minX), (h * 0.80) / Math.Max(1.0, maxY - minY));

            Point W2S(double wx, double wy) => new((wx - cx) * scale + w * 0.5, -(wy - cy) * scale + h * 0.48);

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
                            if (sPts.All(p => p.X < -60 || p.X > w + 60 || p.Y < -60 || p.Y > h + 60)) continue;
                            ctx.BeginFigure(sPts[0], true, true);
                            ctx.PolyLineTo(sPts.Skip(1).ToArray(), false, false);
                            hasLoop = true;
                        }
                    }
                    if (!hasLoop) continue;
                    geo.Freeze();
                    Color hc = _settings.GrayscaleDwgUnderlay
                        ? Color.FromArgb(hatch.IsSolid ? (byte)45 : (byte)20, 148, 163, 184)
                        : Color.FromArgb(hatch.IsSolid ? (byte)155 : (byte)55, hatch.Color.R, hatch.Color.G, hatch.Color.B);
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
            }

            if (_points.Count >= 1)
            {
                var sPts = _points.Select(p => W2S(p.Easting, p.Northing)).ToList();
                if (sPts.Count >= 2)
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(sPts[0], sPts.Count >= 3, sPts.Count >= 3);
                        ctx.PolyLineTo(sPts.Skip(1).ToList(), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(sPts.Count >= 3 ? new SolidColorBrush(Color.FromArgb(40, 2, 132, 199)) : null,
                        new Pen(new SolidColorBrush(Color.FromRgb(185, 28, 28)), 2.0), geo);
                }

                if (_settings.ShowEdgeDistances)
                {
                    foreach (var edge in _metrics.Edges)
                    {
                        Point mp = W2S(edge.MidX, edge.MidY);
                        double len = _settings.UseGroundArea ? edge.GroundLengthMeters : edge.PlanarLengthMeters;
                        DrawPrintLabelShared(dc, this, mp, $"{PersianTextHelper.FormatNumber(len, 2, _persianDigits)} m", Colors.White, Color.FromRgb(15, 23, 42), 8.5);
                    }
                }

                for (int i = 0; i < _points.Count; i++)
                {
                    Point sp = sPts[i];
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(185, 28, 28)), new Pen(Brushes.White, 1.2), sp, 3.6, 3.6);
                    DrawPrintLabelShared(dc, this, new Point(sp.X + 11, sp.Y - 10), _points[i].Label, Color.FromRgb(15, 23, 42), Colors.White, 9, FontWeights.Bold);
                }

                if (_metrics.IsPolygonClosed)
                {
                    Point center = W2S(_metrics.CentroidX, _metrics.CentroidY);
                    double area = _settings.UseGroundArea ? _metrics.GroundAreaSqMeters : _metrics.PlanarAreaSqMeters;
                    DrawPrintLabelShared(dc, this, center, $"S = {PersianTextHelper.FormatNumber(area, 2, _persianDigits)} m²", Color.FromRgb(224, 242, 254), Color.FromRgb(12, 74, 110), 9.5, FontWeights.Bold);
                }
            }

            DrawNorthArrowShared(dc, this, new Point(28, 38), lightBg: true);
            DrawScaleBarShared(dc, this, new Point(18, h - 20), scale, lightBg: true);
        }
    }

    /// <summary>المان نقشه ماهواره‌ای برای چاپ رسمی (همراه با تایل‌های واقعی، محدوده، گوشه‌ها، فلش شمال و مقیاس)</summary>
    private sealed class PrintSatelliteMapElement : FrameworkElement
    {
        private readonly IReadOnlyList<SurveyPoint> _points;
        private readonly PolygonMetrics _metrics;
        private readonly PrintSettings _settings;
        private readonly bool _persianDigits;
        private readonly int _zone;
        private readonly bool _isNorthern;
        private readonly double _fallbackLat;
        private readonly double _fallbackLon;

        public PrintSatelliteMapElement(IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, PrintSettings settings, bool persianDigits, int zone, bool isNorthern, double fallbackLat, double fallbackLon)
        {
            FlowDirection = FlowDirection.LeftToRight;
            _points = points;
            _metrics = metrics;
            _settings = settings;
            _persianDigits = persianDigits;
            _zone = zone;
            _isNorthern = isNorthern;
            _fallbackLat = fallbackLat;
            _fallbackLon = fallbackLon;
            ClipToBounds = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            if (w <= 10 || h <= 10) return;

            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(0, 0, w, h));

            double cLat = _fallbackLat, cLon = _fallbackLon, zoom = 16.5;
            if (_points.Count > 0)
            {
                double minLat = _points.Min(p => p.Latitude);
                double maxLat = _points.Max(p => p.Latitude);
                double minLon = _points.Min(p => p.Longitude);
                double maxLon = _points.Max(p => p.Longitude);
                cLat = (minLat + maxLat) * 0.5;
                cLon = (minLon + maxLon) * 0.5;

                double latSpanM = Math.Max(35.0, (maxLat - minLat) * 111320.0);
                double lonSpanM = Math.Max(35.0, (maxLon - minLon) * 111320.0 * Math.Cos(cLat * Math.PI / 180.0));
                double mpp = Math.Max(lonSpanM / (w * 0.72), latSpanM / (h * 0.72));
                zoom = SatelliteTileService.ZoomFromMetersPerPixel(cLat, mpp);
            }

            int maxZ = SatelliteTileService.GetMaxZoom(_settings.SatelliteProvider);
            int tileZ = Math.Clamp((int)Math.Floor(zoom), 2, maxZ);
            double zoomScale = Math.Pow(2.0, zoom - tileZ);
            double tileScreenSize = 256.0 * zoomScale;

            var (cpx, cpy) = SatelliteTileService.LatLonToWorldPixels(cLat, cLon, tileZ);
            int minTx = (int)Math.Floor((cpx - (w * 0.5) / zoomScale) / 256.0);
            int maxTx = (int)Math.Floor((cpx + (w * 0.5) / zoomScale) / 256.0);
            int minTy = (int)Math.Floor((cpy - (h * 0.5) / zoomScale) / 256.0);
            int maxTy = (int)Math.Floor((cpy + (h * 0.5) / zoomScale) / 256.0);
            int maxIdx = 1 << tileZ;

            for (int tx = minTx; tx <= maxTx; tx++)
            {
                for (int ty = minTy; ty <= maxTy; ty++)
                {
                    if (ty < 0 || ty >= maxIdx) continue;
                    int wx = ((tx % maxIdx) + maxIdx) % maxIdx;
                    double sx = w * 0.5 + (tx * 256.0 - cpx) * zoomScale;
                    double sy = h * 0.5 + (ty * 256.0 - cpy) * zoomScale;
                    var rect = new Rect(Math.Floor(sx), Math.Floor(sy), Math.Ceiling(tileScreenSize) + 0.8, Math.Ceiling(tileScreenSize) + 0.8);

                    var bmp = SatelliteTileService.GetTileForPrint(_settings.SatelliteProvider, tileZ, wx, ty);
                    if (bmp != null)
                        dc.DrawImage(bmp, rect);
                }
            }

            Point G2S(double lat, double lon)
            {
                var (px, py) = SatelliteTileService.LatLonToWorldPixels(lat, lon, tileZ);
                return new Point(w * 0.5 + (px - cpx) * zoomScale, h * 0.5 + (py - cpy) * zoomScale);
            }

            if (_points.Count >= 1)
            {
                var sPts = _points.Select(p => G2S(p.Latitude, p.Longitude)).ToList();
                if (sPts.Count >= 2)
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(sPts[0], sPts.Count >= 3, sPts.Count >= 3);
                        ctx.PolyLineTo(sPts.Skip(1).ToList(), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(sPts.Count >= 3 ? new SolidColorBrush(Color.FromArgb(65, 14, 165, 233)) : null,
                        new Pen(new SolidColorBrush(Color.FromRgb(250, 204, 21)), 2.2), geo);
                }

                if (_settings.ShowEdgeDistances)
                {
                    foreach (var edge in _metrics.Edges)
                    {
                        var mg = CoordinateConverter.UtmToLatLon(edge.MidX, edge.MidY, _zone, _isNorthern);
                        Point mp = G2S(mg.Latitude, mg.Longitude);
                        double len = _settings.UseGroundArea ? edge.GroundLengthMeters : edge.PlanarLengthMeters;
                        DrawPrintLabelShared(dc, this, mp, $"{PersianTextHelper.FormatNumber(len, 2, _persianDigits)} m",
                            Color.FromArgb(220, 15, 23, 42), Color.FromRgb(250, 204, 21), 8.5);
                    }
                }

                for (int i = 0; i < _points.Count; i++)
                {
                    Point sp = sPts[i];
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(239, 68, 68)), new Pen(Brushes.White, 1.3), sp, 3.8, 3.8);
                    DrawPrintLabelShared(dc, this, new Point(sp.X + 11, sp.Y - 10), _points[i].Label,
                        Color.FromRgb(15, 23, 42), Colors.White, 9, FontWeights.Bold);
                }

                if (_metrics.IsPolygonClosed)
                {
                    var cg = CoordinateConverter.UtmToLatLon(_metrics.CentroidX, _metrics.CentroidY, _zone, _isNorthern);
                    Point center = G2S(cg.Latitude, cg.Longitude);
                    double area = _settings.UseGroundArea ? _metrics.GroundAreaSqMeters : _metrics.PlanarAreaSqMeters;
                    DrawPrintLabelShared(dc, this, center, $"S = {PersianTextHelper.FormatNumber(area, 2, _persianDigits)} m²",
                        Color.FromArgb(225, 2, 132, 199), Colors.White, 9.5, FontWeights.Bold);
                }
            }

            double pxPerMeter = 1.0 / SatelliteTileService.MetersPerPixel(cLat, zoom);
            DrawNorthArrowShared(dc, this, new Point(28, 38), lightBg: false);
            DrawScaleBarShared(dc, this, new Point(18, h - 20), pxPerMeter, lightBg: false);
        }
    }
}
