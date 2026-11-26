using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NegarestanCAD.Models;

public class SurveyPoint : INotifyPropertyChanged
{
    private int _orderIndex;
    private string _label = "A";
    private double _easting;
    private double _northing;
    private double _latitude;
    private double _longitude;
    private int _utmZone = 41;
    private bool _isNorthernHemisphere = true;
    private string _description = string.Empty;

    public int OrderIndex
    {
        get => _orderIndex;
        set { if (_orderIndex != value) { _orderIndex = value; OnPropertyChanged(); } }
    }

    public string Label
    {
        get => _label;
        set { if (_label != value) { _label = value; OnPropertyChanged(); } }
    }

    /// <summary>مختصات X شرقی (Easting) به متر</summary>
    public double Easting
    {
        get => _easting;
        set { if (Math.Abs(_easting - value) > 1e-9) { _easting = value; OnPropertyChanged(); } }
    }

    /// <summary>مختصات Y شمالی (Northing) به متر</summary>
    public double Northing
    {
        get => _northing;
        set { if (Math.Abs(_northing - value) > 1e-9) { _northing = value; OnPropertyChanged(); } }
    }

    /// <summary>عرض جغرافیایی (Latitude) به درجه اعشاری</summary>
    public double Latitude
    {
        get => _latitude;
        set { if (Math.Abs(_latitude - value) > 1e-12) { _latitude = value; OnPropertyChanged(); } }
    }

    /// <summary>طول جغرافیایی (Longitude) به درجه اعشاری</summary>
    public double Longitude
    {
        get => _longitude;
        set { if (Math.Abs(_longitude - value) > 1e-12) { _longitude = value; OnPropertyChanged(); } }
    }

    public int UtmZone
    {
        get => _utmZone;
        set { if (_utmZone != value) { _utmZone = value; OnPropertyChanged(); OnPropertyChanged(nameof(ZoneDisplay)); } }
    }

    public bool IsNorthernHemisphere
    {
        get => _isNorthernHemisphere;
        set { if (_isNorthernHemisphere != value) { _isNorthernHemisphere = value; OnPropertyChanged(); OnPropertyChanged(nameof(ZoneDisplay)); } }
    }

    public string Description
    {
        get => _description;
        set { if (_description != value) { _description = value; OnPropertyChanged(); } }
    }

    public string ZoneDisplay => $"{UtmZone}{(IsNorthernHemisphere ? "N" : "S")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
