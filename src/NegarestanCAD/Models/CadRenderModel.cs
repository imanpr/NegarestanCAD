using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NegarestanCAD.Models;

public readonly record struct RgbColor(byte R, byte G, byte B);

public readonly record struct WorldPoint(double X, double Y);

public enum SnapPointKind
{
    Endpoint,
    Midpoint,
    Center,
    Vertex
}

public readonly record struct SnapCandidate(double X, double Y, SnapPointKind Kind, string LayerName);

public class CadPolylineDrawable
{
    public string LayerName { get; set; } = "0";
    public RgbColor Color { get; set; } = new(220, 220, 220);
    public bool IsDefaultWhite { get; set; }
    public bool IsClosed { get; set; }
    public bool IsFilled { get; set; }
    public List<WorldPoint> Points { get; set; } = new();
    public double WorldWidth { get; set; }
}

public class CadHatchDrawable
{
    public string LayerName { get; set; } = "0";
    public RgbColor Color { get; set; } = new(220, 220, 220);
    public bool IsDefaultWhite { get; set; }
    public bool IsSolid { get; set; } = true;
    public List<List<WorldPoint>> Loops { get; set; } = new();
}

public class CadCircleDrawable
{
    public string LayerName { get; set; } = "0";
    public RgbColor Color { get; set; } = new(220, 220, 220);
    public bool IsDefaultWhite { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Radius { get; set; }
}

public class CadTextDrawable
{
    public string LayerName { get; set; } = "0";
    public RgbColor Color { get; set; } = new(220, 220, 220);
    public bool IsDefaultWhite { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Height { get; set; } = 1.0;
    public double RotationDegrees { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string CleanedText { get; set; } = string.Empty;
}

public class CadLayerItem : INotifyPropertyChanged
{
    private bool _isVisible = true;

    public string Name { get; set; } = "0";
    public RgbColor Color { get; set; } = new(220, 220, 220);
    public int EntityCount { get; set; }

    public string ColorHex => $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class CadRenderModel
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public List<CadPolylineDrawable> Polylines { get; set; } = new();
    public List<CadCircleDrawable> Circles { get; set; } = new();
    public List<CadTextDrawable> Texts { get; set; } = new();
    public List<CadLayerItem> Layers { get; set; } = new();
    public List<SnapCandidate> SnapPoints { get; set; } = new();

    public List<CadHatchDrawable> Hatches { get; set; } = new();

    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public bool HasGeometry { get; set; }
    public int TotalEntityCount { get; set; }
}
