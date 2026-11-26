using NegarestanCAD.Models;

namespace NegarestanCAD.Services;

/// <summary>
/// محاسبات هندسی محدوده نقاط: فواصل اضلاع، آزیموت، زوایای داخلی، مساحت مسطح و واقعی زمین، و مرتب‌سازی ساعتگرد
/// </summary>
public static class GeometryCalculator
{
    /// <summary>تولید برچسب انگلیسی گوشه‌ها: A, B, C, ..., Z, AA, AB, ...</summary>
    public static string IndexToAlphabetLabel(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0) return "A";
        string result = string.Empty;
        int n = zeroBasedIndex;
        while (n >= 0)
        {
            result = (char)('A' + (n % 26)) + result;
            n = (n / 26) - 1;
        }
        return result;
    }

    /// <summary>به‌روزرسانی خودکار شماره ترتیب و برچسب‌های انگلیسی (A, B, C...)</summary>
    public static void ReindexAndRelabel(IList<SurveyPoint> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            points[i].OrderIndex = i + 1;
            points[i].Label = IndexToAlphabetLabel(i);
        }
    }

    /// <summary>محاسبه کامل اضلاع، زوایا، محیط، مساحت مسطح و مساحت واقعی زمین</summary>
    public static PolygonMetrics CalculateMetrics(IReadOnlyList<SurveyPoint> points)
    {
        var metrics = new PolygonMetrics();
        if (points == null || points.Count == 0)
            return metrics;

        int n = points.Count;
        metrics.CentroidX = points.Average(p => p.Easting);
        metrics.CentroidY = points.Average(p => p.Northing);

        if (n == 1)
            return metrics;

        bool isClosed = n >= 3;
        metrics.IsPolygonClosed = isClosed;
        int edgeCount = isClosed ? n : n - 1;

        double sumPlanarPerimeter = 0;
        double sumGroundPerimeter = 0;
        double sumScaleFactors = 0;

        for (int i = 0; i < edgeCount; i++)
        {
            var p1 = points[i];
            var p2 = points[(i + 1) % n];

            double dx = p2.Easting - p1.Easting;
            double dy = p2.Northing - p1.Northing;
            double planarLen = Math.Sqrt(dx * dx + dy * dy);

            double k1 = CoordinateConverter.ComputeUtmPointScaleFactor(p1.Easting, p1.Northing, p1.UtmZone, p1.IsNorthernHemisphere);
            double k2 = CoordinateConverter.ComputeUtmPointScaleFactor(p2.Easting, p2.Northing, p2.UtmZone, p2.IsNorthernHemisphere);
            double midX = (p1.Easting + p2.Easting) * 0.5;
            double midY = (p1.Northing + p2.Northing) * 0.5;
            double km = CoordinateConverter.ComputeUtmPointScaleFactor(midX, midY, p1.UtmZone, p1.IsNorthernHemisphere);

            // فرمول سیمپسون برای ضریب مقیاس خطی UTM
            double lineScale = (k1 + 4.0 * km + k2) / 6.0;
            double groundLen = planarLen / lineScale;

            // آزیموت از شمال ساعتگرد (0 تا 360 درجه)
            double azRad = Math.Atan2(dx, dy);
            double azDeg = azRad * 180.0 / Math.PI;
            if (azDeg < 0) azDeg += 360.0;

            metrics.Edges.Add(new EdgeMetric
            {
                FromLabel = p1.Label,
                ToLabel = p2.Label,
                PlanarLengthMeters = planarLen,
                GroundLengthMeters = groundLen,
                ScaleFactor = lineScale,
                AzimuthDegrees = azDeg,
                MidX = midX,
                MidY = midY
            });

            sumPlanarPerimeter += planarLen;
            sumGroundPerimeter += groundLen;
            sumScaleFactors += lineScale;
        }

        metrics.PlanarPerimeterMeters = sumPlanarPerimeter;
        metrics.GroundPerimeterMeters = sumGroundPerimeter;
        metrics.AverageScaleFactor = edgeCount > 0 ? sumScaleFactors / edgeCount : 1.0;

        if (isClosed)
        {
            // محاسبه مساحت با فرمول دقیق Shoelace (با انتقال به مرکز هندسی برای جلوگیری از خطای ممیز شناور در اعداد بزرگ UTM)
            double cx = metrics.CentroidX;
            double cy = metrics.CentroidY;
            double signedTwiceArea = 0;

            for (int i = 0; i < n; i++)
            {
                var p1 = points[i];
                var p2 = points[(i + 1) % n];
                double x1 = p1.Easting - cx;
                double y1 = p1.Northing - cy;
                double x2 = p2.Easting - cx;
                double y2 = p2.Northing - cy;
                signedTwiceArea += (x1 * y2 - x2 * y1);
            }

            double planarArea = Math.Abs(signedTwiceArea) * 0.5;
            double kCentroid = CoordinateConverter.ComputeUtmPointScaleFactor(cx, cy, points[0].UtmZone, points[0].IsNorthernHemisphere);
            double groundArea = planarArea / (kCentroid * kCentroid);

            metrics.PlanarAreaSqMeters = planarArea;
            metrics.GroundAreaSqMeters = groundArea;
            metrics.AverageScaleFactor = kCentroid;
            metrics.HasSelfIntersection = DetectSelfIntersection(points);

            // محاسبه زوایای داخلی در هر رأس
            bool isCcw = signedTwiceArea > 0;
            for (int i = 0; i < n; i++)
            {
                var prev = points[(i - 1 + n) % n];
                var curr = points[i];
                var next = points[(i + 1) % n];

                double v1x = prev.Easting - curr.Easting;
                double v1y = prev.Northing - curr.Northing;
                double v2x = next.Easting - curr.Easting;
                double v2y = next.Northing - curr.Northing;

                double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
                double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                double angleDeg = 0;

                if (len1 > 1e-9 && len2 > 1e-9)
                {
                    double dot = (v1x * v2x + v1y * v2y) / (len1 * len2);
                    dot = Math.Clamp(dot, -1.0, 1.0);
                    double innerAngle = Math.Acos(dot) * 180.0 / Math.PI;

                    // بررسی مقعر بودن گوشه با ضرب خارجی
                    double cross = v1x * v2y - v1y * v2x;
                    bool isReflex = isCcw ? (cross > 0) : (cross < 0);
                    angleDeg = isReflex ? (360.0 - innerAngle) : innerAngle;
                }

                metrics.CornerAngles.Add(new CornerAngleMetric
                {
                    Label = curr.Label,
                    X = curr.Easting,
                    Y = curr.Northing,
                    InteriorAngleDegrees = angleDeg
                });
            }
        }

        return metrics;
    }

    /// <summary>مرتب‌سازی نقاط به صورت ساعتگرد یا پادساعتگرد حول مرکز هندسی برای جلوگیری از تقاطع اضلاع</summary>
    public static List<SurveyPoint> SortPointsRadially(IEnumerable<SurveyPoint> points, bool clockwise = true)
    {
        var list = points.ToList();
        if (list.Count < 3) return list;

        double cx = list.Average(p => p.Easting);
        double cy = list.Average(p => p.Northing);

        // شروع از نقطه شمال‌غربی برای نظم مهندسی (A در بالا-چپ)
        var ordered = clockwise
            ? list.OrderByDescending(p => Math.Atan2(p.Northing - cy, p.Easting - cx)).ToList()
            : list.OrderBy(p => Math.Atan2(p.Northing - cy, p.Easting - cx)).ToList();

        return ordered;
    }

    /// <summary>تشخیص تقاطع اضلاع چندضلعی (Self-Intersection)</summary>
    public static bool DetectSelfIntersection(IReadOnlyList<SurveyPoint> pts)
    {
        int n = pts.Count;
        if (n < 4) return false;

        for (int i = 0; i < n; i++)
        {
            var a1 = pts[i];
            var a2 = pts[(i + 1) % n];

            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue; // اضلاع مجاور
                var b1 = pts[j];
                var b2 = pts[(j + 1) % n];

                if (SegmentsIntersect(a1.Easting, a1.Northing, a2.Easting, a2.Northing,
                                      b1.Easting, b1.Northing, b2.Easting, b2.Northing))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(double x1, double y1, double x2, double y2,
                                          double x3, double y3, double x4, double y4)
    {
        double d1 = Cross(x3, y3, x4, y4, x1, y1);
        double d2 = Cross(x3, y3, x4, y4, x2, y2);
        double d3 = Cross(x1, y1, x2, y2, x3, y3);
        double d4 = Cross(x1, y1, x2, y2, x4, y4);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(double ax, double ay, double bx, double by, double cx, double cy)
        => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
}
