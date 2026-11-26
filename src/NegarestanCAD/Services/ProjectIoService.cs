using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;
using NegarestanCAD.Models;

namespace NegarestanCAD.Services;

/// <summary>
/// سرویس ذخیره و بازیابی فایل پروژه اختصاصی (.cadproj) و خروجی/ورودی CSV/TXT، Excel (.xlsx)، DXF و KML/KMZ
/// </summary>
public static class ProjectIoService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    #region فایل پروژه اختصاصی (.cadproj)

    public static void SaveProject(string filePath, ProjectDocument project)
    {
        project.SavedAtUtc = DateTime.UtcNow;
        string json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(filePath, json, Encoding.UTF8);
    }

    public static ProjectDocument LoadProject(string filePath)
    {
        string json = File.ReadAllText(filePath, Encoding.UTF8);
        var doc = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions)
                  ?? throw new InvalidDataException("ساختار فایل پروژه نامعتبر است.");

        GeometryCalculator.ReindexAndRelabel(doc.Points);
        return doc;
    }

    #endregion

    #region CSV / TXT

    public static void ExportCsv(string filePath, IReadOnlyList<SurveyPoint> points)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Label,Easting_X,Northing_Y,Zone,Hemisphere,Latitude,Longitude,Description");

        foreach (var p in points)
        {
            string desc = (p.Description ?? string.Empty).Replace("\"", "\"\"");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:F4},{2:F4},{3},{4},{5:F8},{6:F8},\"{7}\"",
                p.Label, p.Easting, p.Northing, p.UtmZone, p.IsNorthernHemisphere ? "N" : "S",
                p.Latitude, p.Longitude, desc));
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static List<SurveyPoint> ImportCsvOrTxt(string filePath, int defaultZone, bool defaultNorthern)
    {
        var lines = File.ReadAllLines(filePath, Encoding.UTF8);
        var points = new List<SurveyPoint>();

        foreach (string rawLine in lines)
        {
            string line = PersianTextHelper.ToEnglishDigits(rawLine.Trim());
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            var parts = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            // رد کردن ردیف سرستون
            if (parts[0].Contains("Label", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Contains("Easting", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Contains("نقطه", StringComparison.OrdinalIgnoreCase))
                continue;

            int idx = 0;
            string? customLabel = null;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                customLabel = parts[0].Trim('"');
                idx = 1;
            }

            if (parts.Length < idx + 2) continue;
            if (!double.TryParse(parts[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out double v1)) continue;
            if (!double.TryParse(parts[idx + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v2)) continue;

            int zone = defaultZone;
            bool isNorth = defaultNorthern;

            if (parts.Length > idx + 2 && int.TryParse(parts[idx + 2], out int parsedZone) && parsedZone is >= 1 and <= 60)
                zone = parsedZone;
            if (parts.Length > idx + 3 && parts[idx + 3].Equals("S", StringComparison.OrdinalIgnoreCase))
                isNorth = false;

            var pt = CreatePointFromAutoDetect(v1, v2, zone, isNorth, customLabel);
            points.Add(pt);
        }

        GeometryCalculator.ReindexAndRelabel(points);
        return points;
    }

    #endregion

    #region Excel (.xlsx) با ClosedXML

    public static void ExportExcel(string filePath, IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, PrintSettings printInfo)
    {
        using var wb = new XLWorkbook();

        // شیت ۱: نقاط مختصات
        var wsPts = wb.Worksheets.Add("مختصات نقاط");
        wsPts.RightToLeft = true;

        wsPts.Cell(1, 1).Value = "گوشه";
        wsPts.Cell(1, 2).Value = "X (Easting - m)";
        wsPts.Cell(1, 3).Value = "Y (Northing - m)";
        wsPts.Cell(1, 4).Value = "زون UTM";
        wsPts.Cell(1, 5).Value = "عرض جغرافیایی (Lat)";
        wsPts.Cell(1, 6).Value = "طول جغرافیایی (Lon)";
        wsPts.Cell(1, 7).Value = "عرض (DMS)";
        wsPts.Cell(1, 8).Value = "طول (DMS)";
        wsPts.Cell(1, 9).Value = "توضیحات";

        var headerRange = wsPts.Range(1, 1, 1, 9);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A5F");
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            int r = i + 2;
            wsPts.Cell(r, 1).Value = p.Label;
            wsPts.Cell(r, 2).Value = Math.Round(p.Easting, 4);
            wsPts.Cell(r, 3).Value = Math.Round(p.Northing, 4);
            wsPts.Cell(r, 4).Value = p.ZoneDisplay;
            wsPts.Cell(r, 5).Value = Math.Round(p.Latitude, 8);
            wsPts.Cell(r, 6).Value = Math.Round(p.Longitude, 8);
            wsPts.Cell(r, 7).Value = CoordinateConverter.ToDmsString(p.Latitude, true);
            wsPts.Cell(r, 8).Value = CoordinateConverter.ToDmsString(p.Longitude, false);
            wsPts.Cell(r, 9).Value = p.Description;
        }
        wsPts.Columns().AdjustToContents();

        // شیت ۲: اضلاع و خلاصه مساحت
        var wsSummary = wb.Worksheets.Add("اضلاع و مساحت");
        wsSummary.RightToLeft = true;

        wsSummary.Cell(1, 1).Value = "ضلع";
        wsSummary.Cell(1, 2).Value = "طول واقعی روی زمین (متر)";
        wsSummary.Cell(1, 3).Value = "طول مسطح UTM (متر)";
        wsSummary.Cell(1, 4).Value = "آزیموت (درجه)";
        wsSummary.Cell(1, 5).Value = "ضریب مقیاس UTM";

        var h2 = wsSummary.Range(1, 1, 1, 5);
        h2.Style.Font.Bold = true;
        h2.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A5F");
        h2.Style.Font.FontColor = XLColor.White;

        for (int i = 0; i < metrics.Edges.Count; i++)
        {
            var e = metrics.Edges[i];
            int r = i + 2;
            wsSummary.Cell(r, 1).Value = e.EdgeName;
            wsSummary.Cell(r, 2).Value = Math.Round(e.GroundLengthMeters, 3);
            wsSummary.Cell(r, 3).Value = Math.Round(e.PlanarLengthMeters, 3);
            wsSummary.Cell(r, 4).Value = Math.Round(e.AzimuthDegrees, 2);
            wsSummary.Cell(r, 5).Value = Math.Round(e.ScaleFactor, 7);
        }

        int summaryStart = metrics.Edges.Count + 4;
        wsSummary.Cell(summaryStart, 1).Value = "عنوان پروژه";
        wsSummary.Cell(summaryStart, 2).Value = printInfo.ProjectTitle;
        wsSummary.Cell(summaryStart + 1, 1).Value = "نام مالک / متقاضی";
        wsSummary.Cell(summaryStart + 1, 2).Value = printInfo.OwnerName;
        wsSummary.Cell(summaryStart + 2, 1).Value = "پلاک ثبتی";
        wsSummary.Cell(summaryStart + 2, 2).Value = printInfo.ParcelNumber;
        wsSummary.Cell(summaryStart + 3, 1).Value = "محیط کل واقعی (متر)";
        wsSummary.Cell(summaryStart + 3, 2).Value = Math.Round(metrics.GroundPerimeterMeters, 3);
        wsSummary.Cell(summaryStart + 4, 1).Value = "مساحت کل واقعی زمین (مترمربع)";
        wsSummary.Cell(summaryStart + 4, 2).Value = Math.Round(metrics.GroundAreaSqMeters, 3);
        wsSummary.Cell(summaryStart + 5, 1).Value = "مساحت کل واقعی (هکتار)";
        wsSummary.Cell(summaryStart + 5, 2).Value = Math.Round(metrics.GroundAreaHectares, 6);
        wsSummary.Cell(summaryStart + 6, 1).Value = "مساحت مسطح UTM (مترمربع)";
        wsSummary.Cell(summaryStart + 6, 2).Value = Math.Round(metrics.PlanarAreaSqMeters, 3);
        wsSummary.Cell(summaryStart + 7, 1).Value = "ضریب مقیاس میانگین UTM";
        wsSummary.Cell(summaryStart + 7, 2).Value = Math.Round(metrics.AverageScaleFactor, 7);

        wsSummary.Range(summaryStart, 1, summaryStart + 7, 1).Style.Font.Bold = true;
        wsSummary.Columns().AdjustToContents();

        wb.SaveAs(filePath);
    }

    public static List<SurveyPoint> ImportExcel(string filePath, int defaultZone, bool defaultNorthern)
    {
        var points = new List<SurveyPoint>();
        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheet(1);
        var rows = ws.RangeUsed()?.RowsUsed();
        if (rows == null) return points;

        foreach (var row in rows.Skip(1))
        {
            string c1 = row.Cell(1).GetString().Trim();
            string c2 = row.Cell(2).GetString().Trim();
            string c3 = row.Cell(3).GetString().Trim();

            double v1, v2;
            string? label = null;
            int zoneCol = 4;

            if (double.TryParse(PersianTextHelper.ToEnglishDigits(c2), NumberStyles.Float, CultureInfo.InvariantCulture, out v1) &&
                double.TryParse(PersianTextHelper.ToEnglishDigits(c3), NumberStyles.Float, CultureInfo.InvariantCulture, out v2))
            {
                label = c1;
            }
            else if (double.TryParse(PersianTextHelper.ToEnglishDigits(c1), NumberStyles.Float, CultureInfo.InvariantCulture, out v1) &&
                     double.TryParse(PersianTextHelper.ToEnglishDigits(c2), NumberStyles.Float, CultureInfo.InvariantCulture, out v2))
            {
                zoneCol = 3;
            }
            else
            {
                continue;
            }

            int zone = defaultZone;
            bool isNorth = defaultNorthern;
            string zoneStr = row.Cell(zoneCol).GetString().Trim();
            if (!string.IsNullOrEmpty(zoneStr))
            {
                if (zoneStr.EndsWith("S", StringComparison.OrdinalIgnoreCase)) isNorth = false;
                string numPart = new string(zoneStr.Where(char.IsDigit).ToArray());
                if (int.TryParse(numPart, out int z) && z is >= 1 and <= 60) zone = z;
            }

            var pt = CreatePointFromAutoDetect(v1, v2, zone, isNorth, label);
            pt.Description = row.Cell(9).GetString().Trim();
            points.Add(pt);
        }

        GeometryCalculator.ReindexAndRelabel(points);
        return points;
    }

    #endregion

    #region Google Earth KML / KMZ

    public static void ExportKmlOrKmz(string filePath, IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, string projectTitle)
    {
        string kmlContent = BuildKmlString(points, metrics, projectTitle);
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".kmz")
        {
            if (File.Exists(filePath)) File.Delete(filePath);
            using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("doc.kml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(kmlContent);
        }
        else
        {
            File.WriteAllText(filePath, kmlContent, Encoding.UTF8);
        }
    }

    private static string BuildKmlString(IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, string projectTitle)
    {
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        var docElem = new XElement(ns + "Document",
            new XElement(ns + "name", string.IsNullOrWhiteSpace(projectTitle) ? "NegarestanCAD Parcel" : projectTitle),
            new XElement(ns + "Style", new XAttribute("id", "parcelStyle"),
                new XElement(ns + "LineStyle",
                    new XElement(ns + "color", "ff0055ff"),
                    new XElement(ns + "width", "3")),
                new XElement(ns + "PolyStyle",
                    new XElement(ns + "color", "4400aaff"))));

        // Placemark برای هر گوشه
        foreach (var p in points)
        {
            docElem.Add(new XElement(ns + "Placemark",
                new XElement(ns + "name", p.Label),
                new XElement(ns + "description",
                    $"UTM ({p.ZoneDisplay}): X={p.Easting:F3}, Y={p.Northing:F3}\nLat={p.Latitude:F7}, Lon={p.Longitude:F7}"),
                new XElement(ns + "Point",
                    new XElement(ns + "coordinates",
                        string.Format(CultureInfo.InvariantCulture, "{0:F8},{1:F8},0", p.Longitude, p.Latitude)))));
        }

        // چندضلعی محدوده
        if (points.Count >= 3)
        {
            var coordList = points
                .Concat(new[] { points[0] })
                .Select(p => string.Format(CultureInfo.InvariantCulture, "{0:F8},{1:F8},0", p.Longitude, p.Latitude));

            docElem.Add(new XElement(ns + "Placemark",
                new XElement(ns + "name", $"{projectTitle} - مساحت {metrics.GroundAreaSqMeters:N2} مترمربع"),
                new XElement(ns + "styleUrl", "#parcelStyle"),
                new XElement(ns + "Polygon",
                    new XElement(ns + "outerBoundaryIs",
                        new XElement(ns + "LinearRing",
                            new XElement(ns + "coordinates", string.Join(" ", coordList)))))));
        }

        var kml = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "kml", docElem));
        return kml.ToString();
    }

    #endregion

    private static SurveyPoint CreatePointFromAutoDetect(double v1, double v2, int zone, bool isNorth, string? label)
    {
        // تشخیص هوشمند: اگر هر دو مقدار در محدوده طول/عرض جغرافیایی باشند (|v1|<=90 و |v2|<=180)
        if (Math.Abs(v1) <= 90.0 && Math.Abs(v2) <= 180.0 && (Math.Abs(v1) < 1000 && Math.Abs(v2) < 1000))
        {
            var utm = CoordinateConverter.LatLonToUtm(v1, v2, zone);
            return new SurveyPoint
            {
                Label = string.IsNullOrWhiteSpace(label) ? "A" : label,
                Latitude = v1,
                Longitude = v2,
                Easting = utm.Easting,
                Northing = utm.Northing,
                UtmZone = utm.Zone,
                IsNorthernHemisphere = utm.IsNorthern
            };
        }
        else
        {
            var geo = CoordinateConverter.UtmToLatLon(v1, v2, zone, isNorth);
            return new SurveyPoint
            {
                Label = string.IsNullOrWhiteSpace(label) ? "A" : label,
                Easting = v1,
                Northing = v2,
                Latitude = geo.Latitude,
                Longitude = geo.Longitude,
                UtmZone = zone,
                IsNorthernHemisphere = isNorth
            };
        }
    }
}
