namespace NegarestanCAD.Models;

public class EdgeMetric
{
    public string FromLabel { get; set; } = string.Empty;
    public string ToLabel { get; set; } = string.Empty;
    public string EdgeName => $"{FromLabel} → {ToLabel}";

    /// <summary>فاصله مسطح روی صفحه UTM به متر</summary>
    public double PlanarLengthMeters { get; set; }

    /// <summary>فاصله واقعی روی زمین پس از اعمال ضریب مقیاس UTM به متر</summary>
    public double GroundLengthMeters { get; set; }

    /// <summary>ضریب مقیاس میانگین ضلع</summary>
    public double ScaleFactor { get; set; } = 1.0;

    /// <summary>آزیموت (گرا) از شمال به صورت ساعتگرد بر حسب درجه</summary>
    public double AzimuthDegrees { get; set; }

    public double MidX { get; set; }
    public double MidY { get; set; }
}

public class CornerAngleMetric
{
    public string Label { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double InteriorAngleDegrees { get; set; }
}

public class PolygonMetrics
{
    public List<EdgeMetric> Edges { get; set; } = new();
    public List<CornerAngleMetric> CornerAngles { get; set; } = new();

    public bool IsPolygonClosed { get; set; }
    public bool HasSelfIntersection { get; set; }

    public double PlanarPerimeterMeters { get; set; }
    public double GroundPerimeterMeters { get; set; }

    public double PlanarAreaSqMeters { get; set; }
    public double GroundAreaSqMeters { get; set; }

    public double PlanarAreaHectares => PlanarAreaSqMeters / 10000.0;
    public double GroundAreaHectares => GroundAreaSqMeters / 10000.0;

    public double AverageScaleFactor { get; set; } = 1.0;

    public double CentroidX { get; set; }
    public double CentroidY { get; set; }
}
