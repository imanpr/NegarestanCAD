using System.Globalization;
using System.Text.RegularExpressions;

namespace NegarestanCAD.Services;

public readonly record struct UtmCoordinate(double Easting, double Northing, int Zone, bool IsNorthern);
public readonly record struct GeoCoordinate(double Latitude, double Longitude);

/// <summary>
/// تبدیل دقیق دوطرفه مختصات UTM و طول/عرض جغرافیایی (WGS84) + محاسبه ضریب مقیاس نقطه‌ای UTM
/// </summary>
public static class CoordinateConverter
{
    // پارامترهای بیضوی WGS84
    private const double SemiMajorAxisA = 6378137.0;
    private const double Flattening = 1.0 / 298.257223563;
    private static readonly double EccSq = Flattening * (2.0 - Flattening); // e^2
    private static readonly double EccPrimeSq = EccSq / (1.0 - EccSq);      // e'^2
    private const double ScaleFactorK0 = 0.9996;
    private const double FalseEasting = 500000.0;
    private const double FalseNorthingSouth = 10000000.0;

    /// <summary>تعیین خودکار شماره زون UTM از روی طول جغرافیایی</summary>
    public static int GetUtmZoneFromLongitude(double longitudeDegrees)
    {
        int zone = (int)Math.Floor((longitudeDegrees + 180.0) / 6.0) + 1;
        return Math.Clamp(zone, 1, 60);
    }

    /// <summary>تبدیل طول و عرض جغرافیایی (WGS84) به مختصات UTM (با قابلیت تعیین زون دلخواه)</summary>
    public static UtmCoordinate LatLonToUtm(double latitudeDegrees, double longitudeDegrees, int? forcedZone = null)
    {
        if (latitudeDegrees < -80.0 || latitudeDegrees > 84.0)
            throw new ArgumentOutOfRangeException(nameof(latitudeDegrees), "عرض جغرافیایی برای سیستم UTM باید بین 80- تا 84+ درجه باشد.");

        int zone = forcedZone ?? GetUtmZoneFromLongitude(longitudeDegrees);
        zone = Math.Clamp(zone, 1, 60);
        bool isNorthern = latitudeDegrees >= 0.0;

        double latRad = DegreesToRadians(latitudeDegrees);
        double lonRad = DegreesToRadians(longitudeDegrees);
        double lonOriginRad = DegreesToRadians((zone - 1) * 6.0 - 180.0 + 3.0);

        double sinLat = Math.Sin(latRad);
        double cosLat = Math.Cos(latRad);
        double tanLat = Math.Tan(latRad);

        double n = SemiMajorAxisA / Math.Sqrt(1.0 - EccSq * sinLat * sinLat);
        double t = tanLat * tanLat;
        double c = EccPrimeSq * cosLat * cosLat;
        double a = cosLat * (lonRad - lonOriginRad);

        double e2 = EccSq;
        double e4 = e2 * e2;
        double e6 = e4 * e2;

        double m = SemiMajorAxisA * (
            (1.0 - e2 / 4.0 - 3.0 * e4 / 64.0 - 5.0 * e6 / 256.0) * latRad
            - (3.0 * e2 / 8.0 + 3.0 * e4 / 32.0 + 45.0 * e6 / 1024.0) * Math.Sin(2.0 * latRad)
            + (15.0 * e4 / 256.0 + 45.0 * e6 / 1024.0) * Math.Sin(4.0 * latRad)
            - (35.0 * e6 / 3072.0) * Math.Sin(6.0 * latRad)
        );

        double a2 = a * a;
        double a3 = a2 * a;
        double a4 = a2 * a2;
        double a5 = a4 * a;
        double a6 = a4 * a2;

        double easting = ScaleFactorK0 * n * (
            a
            + (1.0 - t + c) * a3 / 6.0
            + (5.0 - 18.0 * t + t * t + 72.0 * c - 58.0 * EccPrimeSq) * a5 / 120.0
        ) + FalseEasting;

        double northing = ScaleFactorK0 * (
            m + n * tanLat * (
                a2 / 2.0
                + (5.0 - t + 9.0 * c + 4.0 * c * c) * a4 / 24.0
                + (61.0 - 58.0 * t + t * t + 600.0 * c - 330.0 * EccPrimeSq) * a6 / 720.0
            )
        );

        if (!isNorthern)
            northing += FalseNorthingSouth;

        return new UtmCoordinate(easting, northing, zone, isNorthern);
    }

    /// <summary>تبدیل مختصات UTM به طول و عرض جغرافیایی (WGS84)</summary>
    public static GeoCoordinate UtmToLatLon(double easting, double northing, int zone, bool isNorthern = true)
    {
        zone = Math.Clamp(zone, 1, 60);

        double x = easting - FalseEasting;
        double y = isNorthern ? northing : northing - FalseNorthingSouth;

        double e2 = EccSq;
        double e4 = e2 * e2;
        double e6 = e4 * e2;
        double e1 = (1.0 - Math.Sqrt(1.0 - e2)) / (1.0 + Math.Sqrt(1.0 - e2));

        double m = y / ScaleFactorK0;
        double mu = m / (SemiMajorAxisA * (1.0 - e2 / 4.0 - 3.0 * e4 / 64.0 - 5.0 * e6 / 256.0));

        double phi1Rad = mu
            + (3.0 * e1 / 2.0 - 27.0 * Math.Pow(e1, 3) / 32.0) * Math.Sin(2.0 * mu)
            + (21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4) / 32.0) * Math.Sin(4.0 * mu)
            + (151.0 * Math.Pow(e1, 3) / 96.0) * Math.Sin(6.0 * mu)
            + (1097.0 * Math.Pow(e1, 4) / 512.0) * Math.Sin(8.0 * mu);

        double sinPhi1 = Math.Sin(phi1Rad);
        double cosPhi1 = Math.Cos(phi1Rad);
        double tanPhi1 = Math.Tan(phi1Rad);

        double n1 = SemiMajorAxisA / Math.Sqrt(1.0 - e2 * sinPhi1 * sinPhi1);
        double t1 = tanPhi1 * tanPhi1;
        double c1 = EccPrimeSq * cosPhi1 * cosPhi1;
        double r1 = SemiMajorAxisA * (1.0 - e2) / Math.Pow(1.0 - e2 * sinPhi1 * sinPhi1, 1.5);
        double d = x / (n1 * ScaleFactorK0);

        double d2 = d * d;
        double d3 = d2 * d;
        double d4 = d2 * d2;
        double d5 = d4 * d;
        double d6 = d4 * d2;

        double latRad = phi1Rad - (n1 * tanPhi1 / r1) * (
            d2 / 2.0
            - (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * EccPrimeSq) * d4 / 24.0
            + (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * EccPrimeSq - 3.0 * c1 * c1) * d6 / 720.0
        );

        double lonOriginDeg = (zone - 1) * 6.0 - 180.0 + 3.0;
        double lonRad = (
            d
            - (1.0 + 2.0 * t1 + c1) * d3 / 6.0
            + (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * EccPrimeSq + 24.0 * t1 * t1) * d5 / 120.0
        ) / cosPhi1;

        double latitude = RadiansToDegrees(latRad);
        double longitude = lonOriginDeg + RadiansToDegrees(lonRad);

        return new GeoCoordinate(latitude, longitude);
    }

    /// <summary>
    /// محاسبه ضریب مقیاس نقطه‌ای UTM (Point Scale Factor k) در یک نقطه برای تبدیل فواصل و مساحت مسطح به مقدار واقعی روی زمین
    /// </summary>
    public static double ComputeUtmPointScaleFactor(double easting, double northing, int zone, bool isNorthern = true)
    {
        var geo = UtmToLatLon(easting, northing, zone, isNorthern);
        double latRad = DegreesToRadians(geo.Latitude);
        double sinLat = Math.Sin(latRad);
        double cosLat = Math.Cos(latRad);

        // شعاع انحنای میانگین بیضوی WGS84 در عرض جغرافیایی نقطه
        double nu = SemiMajorAxisA / Math.Sqrt(1.0 - EccSq * sinLat * sinLat);
        double rho = SemiMajorAxisA * (1.0 - EccSq) / Math.Pow(1.0 - EccSq * sinLat * sinLat, 1.5);
        double rmSq = nu * rho;

        double dx = easting - FalseEasting;
        double dx2 = dx * dx;
        double k02 = ScaleFactorK0 * ScaleFactorK0;

        // فرمول استاندارد ضریب مقیاس نقطه‌ای در سیستم UTM
        double k = ScaleFactorK0 * (1.0 + dx2 / (2.0 * k02 * rmSq) + (dx2 * dx2) / (24.0 * k02 * k02 * rmSq * rmSq));
        return k;
    }

    /// <summary>تبدیل درجه اعشاری به فرمت درجه-دقیقه-ثانیه (DMS)</summary>
    public static string ToDmsString(double decimalDegrees, bool isLatitude)
    {
        string hemi = isLatitude
            ? (decimalDegrees >= 0 ? "N" : "S")
            : (decimalDegrees >= 0 ? "E" : "W");

        double absVal = Math.Abs(decimalDegrees);
        int deg = (int)Math.Floor(absVal);
        double minFloat = (absVal - deg) * 60.0;
        int min = (int)Math.Floor(minFloat);
        double sec = (minFloat - min) * 60.0;

        if (sec >= 59.995)
        {
            sec = 0;
            min++;
            if (min >= 60) { min = 0; deg++; }
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}° {1:00}' {2:00.00}\" {3}", deg, min, sec, hemi);
    }

    /// <summary>پارس هوشمند مختصات جغرافیایی (هم اعشاری مانند 36.297 و هم DMS مانند 36° 17' 49.2" N)</summary>
    public static bool TryParseGeoAngle(string input, out double decimalDegrees)
    {
        decimalDegrees = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string normalized = PersianTextHelper.ToEnglishDigits(input.Trim())
            .Replace("،", ".")
            .Replace(",", ".");

        // تلاش اول: عدد اعشاری ساده
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
        {
            decimalDegrees = plain;
            return true;
        }

        // تلاش دوم: فرمت DMS مثل 36° 17' 49.2" N یا 36 17 49.2
        bool isNegative = normalized.Contains('-') ||
                          normalized.Contains('S', StringComparison.OrdinalIgnoreCase) ||
                          normalized.Contains('W', StringComparison.OrdinalIgnoreCase);

        var matches = Regex.Matches(normalized, @"\d+(?:\.\d+)?");
        if (matches.Count >= 1 && matches.Count <= 3)
        {
            double deg = double.Parse(matches[0].Value, CultureInfo.InvariantCulture);
            double min = matches.Count >= 2 ? double.Parse(matches[1].Value, CultureInfo.InvariantCulture) : 0.0;
            double sec = matches.Count >= 3 ? double.Parse(matches[2].Value, CultureInfo.InvariantCulture) : 0.0;

            decimalDegrees = deg + min / 60.0 + sec / 3600.0;
            if (isNegative) decimalDegrees = -decimalDegrees;
            return true;
        }

        return false;
    }

    private static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;
    private static double RadiansToDegrees(double rad) => rad * 180.0 / Math.PI;
}
