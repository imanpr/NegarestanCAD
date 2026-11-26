namespace NegarestanCAD.Models;

public enum PaperSizeOption
{
    A4,
    A3
}

public enum PaperOrientationOption
{
    Landscape,
    Portrait
}

public class PrintSettings
{
    public PaperSizeOption PaperSize { get; set; } = PaperSizeOption.A4;
    public PaperOrientationOption Orientation { get; set; } = PaperOrientationOption.Landscape;

    public string ProjectTitle { get; set; } = "نقشه تفکیکی و تعیین محدوده UTM";
    public string OwnerName { get; set; } = string.Empty;
    public string ParcelNumber { get; set; } = string.Empty;
    public string LocationAddress { get; set; } = string.Empty;
    public string EngineerOrCompany { get; set; } = "شهرداری بریس";
    public string EngineerLicenseNumber { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;
    public string CustomDateShamsi { get; set; } = string.Empty;
    public string ScaleLabelOverride { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public bool IncludeDwgUnderlay { get; set; } = true;
    public bool GrayscaleDwgUnderlay { get; set; } = true;
    public bool ShowEdgeDistances { get; set; } = true;
    public bool ShowInteriorAngles { get; set; } = true;
    public bool ShowCoordinatesTable { get; set; } = true;
    public bool ShowEdgesTable { get; set; } = true;
    public bool UseGroundArea { get; set; } = true;
}

public class AppSettings
{
    public bool IsDarkCanvas { get; set; } = true;
    public bool UsePersianDigits { get; set; } = false;
    public bool UseGroundMeasurements { get; set; } = true;
    public bool FixLegacyPersianShx { get; set; } = true;
    public bool EnableSnap { get; set; } = true;
    public bool ShowInteriorAngles { get; set; } = true;
    public bool ShowEdgeDistances { get; set; } = true;
    public int DefaultUtmZone { get; set; } = 41;
    public bool DefaultIsNorthern { get; set; } = true;
    public int CoordinateDecimals { get; set; } = 3;
}

public class ProjectDocument
{
    public string FormatVersion { get; set; } = "1.0";
    public string ProjectName { get; set; } = "پروژه بدون عنوان";
    public string? LinkedCadFilePath { get; set; }
    public int UtmZone { get; set; } = 41;
    public bool IsNorthernHemisphere { get; set; } = true;
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public List<SurveyPoint> Points { get; set; } = new();
    public PrintSettings PrintSettings { get; set; } = new();
    public AppSettings AppSettings { get; set; } = new();
}
