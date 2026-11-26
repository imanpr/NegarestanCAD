using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;
using NegarestanCAD.Models;
using System.IO;

namespace NegarestanCAD.Services;

/// <summary>
/// سرویس خواندن فایل‌های DWG و DXF با ACadSharp و تبدیل آن‌ها به مدل سبک رندر و نقاط Snap، و همچنین خروجی DXF
/// </summary>
public static class CadFileService
{
    private const int MaxBlockRecursionDepth = 8;

    public static CadRenderModel LoadCadFile(string filePath, bool fixLegacyPersianShx = true)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("فایل نقشه پیدا نشد.", filePath);

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        CadDocument doc = ext switch
        {
            ".dwg" => DwgReader.Read(filePath),
            ".dxf" => DxfReader.Read(filePath),
            _ => throw new NotSupportedException($"فرمت '{ext}' پشتیبانی نمی‌شود. لطفاً فایل DWG یا DXF انتخاب کنید.")
        };

        var model = new CadRenderModel
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath)
        };

        var layerMap = new Dictionary<string, CadLayerItem>(StringComparer.OrdinalIgnoreCase);
        foreach (Layer layer in doc.Layers)
        {
            var color = ResolveAciOrTrueColor(layer.Color, out _);
            layerMap[layer.Name] = new CadLayerItem
            {
                Name = layer.Name,
                //IsVisible = layer.IsOn,
                IsVisible = layer.IsOn && (((int)layer.Flags & 1) == 0),
                Color = color,
                EntityCount = 0
            };
        }

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        void UpdateBounds(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            model.HasGeometry = true;
        }

        var identity = Matrix2D.Identity;
        foreach (Entity entity in doc.Entities)
        {
            ExtractEntity(entity, identity, null, null, 0, model, layerMap, fixLegacyPersianShx, UpdateBounds);
        }

        model.Layers = layerMap.Values
            .OrderByDescending(l => l.EntityCount)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (model.HasGeometry)
        {
            model.MinX = minX;
            model.MinY = minY;
            model.MaxX = maxX;
            model.MaxY = maxY;
        }

        return model;
    }

    private static void ExtractEntity(
        Entity entity,
        Matrix2D transform,
        string? parentLayerName,
        RgbColor? parentBlockColor,
        int depth,
        CadRenderModel model,
        Dictionary<string, CadLayerItem> layerMap,
        bool fixLegacyPersianShx,
        Action<double, double> updateBounds)
    {
        if (entity == null || entity.IsInvisible) return;

        string layerName = entity.Layer?.Name ?? parentLayerName ?? "0";
        if (layerName == "0" && !string.IsNullOrEmpty(parentLayerName))
            layerName = parentLayerName;

        if (!layerMap.TryGetValue(layerName, out var layerItem))
        {
            layerItem = new CadLayerItem { Name = layerName, IsVisible = true, Color = new RgbColor(220, 220, 220) };
            layerMap[layerName] = layerItem;
        }

        RgbColor color = ResolveEntityColor(entity, layerItem.Color, parentBlockColor, out bool isDefaultWhite);

        switch (entity)
        {
            case Line line:
            {
                var p1 = transform.Apply(line.StartPoint.X, line.StartPoint.Y);
                var p2 = transform.Apply(line.EndPoint.X, line.EndPoint.Y);
                model.Polylines.Add(new CadPolylineDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    IsClosed = false,
                    Points = new List<WorldPoint> { p1, p2 }
                });
                updateBounds(p1.X, p1.Y);
                updateBounds(p2.X, p2.Y);

                model.SnapPoints.Add(new SnapCandidate(p1.X, p1.Y, SnapPointKind.Endpoint, layerName));
                model.SnapPoints.Add(new SnapCandidate(p2.X, p2.Y, SnapPointKind.Endpoint, layerName));
                model.SnapPoints.Add(new SnapCandidate((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5, SnapPointKind.Midpoint, layerName));

                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case LwPolyline lwPoly:
            {
                if (lwPoly.Vertices.Count < 2) break;
                var pts = new List<WorldPoint>();
                int count = lwPoly.Vertices.Count;
                int segCount = lwPoly.IsClosed ? count : count - 1;

                for (int i = 0; i < segCount; i++)
                {
                    var v1 = lwPoly.Vertices[i];
                    var v2 = lwPoly.Vertices[(i + 1) % count];

                    var wp1 = transform.Apply(v1.Location.X, v1.Location.Y);
                    var wp2 = transform.Apply(v2.Location.X, v2.Location.Y);

                    if (i == 0) pts.Add(wp1);
                    model.SnapPoints.Add(new SnapCandidate(wp1.X, wp1.Y, SnapPointKind.Vertex, layerName));

                    if (Math.Abs(v1.Bulge) > 1e-6)
                    {
                        var arcPts = TessellateBulge(v1.Location.X, v1.Location.Y, v2.Location.X, v2.Location.Y, v1.Bulge, 16);
                        for (int k = 1; k < arcPts.Count; k++)
                        {
                            var ap = transform.Apply(arcPts[k].X, arcPts[k].Y);
                            pts.Add(ap);
                            updateBounds(ap.X, ap.Y);
                        }
                    }
                    else
                    {
                        pts.Add(wp2);
                        model.SnapPoints.Add(new SnapCandidate((wp1.X + wp2.X) * 0.5, (wp1.Y + wp2.Y) * 0.5, SnapPointKind.Midpoint, layerName));
                    }

                    updateBounds(wp1.X, wp1.Y);
                    updateBounds(wp2.X, wp2.Y);
                }

                if (!lwPoly.IsClosed && count > 0)
                {
                    var last = transform.Apply(lwPoly.Vertices[^1].Location.X, lwPoly.Vertices[^1].Location.Y);
                    model.SnapPoints.Add(new SnapCandidate(last.X, last.Y, SnapPointKind.Endpoint, layerName));
                }

                    double wWidth = lwPoly.ConstantWidth > 0
                        ? lwPoly.ConstantWidth
                        : (lwPoly.Vertices.Count > 0 ? lwPoly.Vertices[0].StartWidth : 0);

                    model.Polylines.Add(new CadPolylineDrawable
                    {
                        LayerName = layerName,
                        Color = color,
                        IsDefaultWhite = isDefaultWhite,
                        IsClosed = lwPoly.IsClosed,
                        WorldWidth = wWidth * transform.ApproximateScale(),
                        Points = pts
                    });

                    layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Polyline2D poly2d:
            {
                var verts = poly2d.Vertices.ToList();
                if (verts.Count < 2) break;
                var pts = new List<WorldPoint>();
                int count = verts.Count;
                int segCount = poly2d.IsClosed ? count : count - 1;

                for (int i = 0; i < segCount; i++)
                {
                    var v1 = verts[i];
                    var v2 = verts[(i + 1) % count];
                    var wp1 = transform.Apply(v1.Location.X, v1.Location.Y);
                    var wp2 = transform.Apply(v2.Location.X, v2.Location.Y);
                    if (i == 0) pts.Add(wp1);
                    model.SnapPoints.Add(new SnapCandidate(wp1.X, wp1.Y, SnapPointKind.Vertex, layerName));

                    if (Math.Abs(v1.Bulge) > 1e-6)
                    {
                        var arcPts = TessellateBulge(v1.Location.X, v1.Location.Y, v2.Location.X, v2.Location.Y, v1.Bulge, 16);
                        for (int k = 1; k < arcPts.Count; k++)
                        {
                            var ap = transform.Apply(arcPts[k].X, arcPts[k].Y);
                            pts.Add(ap);
                            updateBounds(ap.X, ap.Y);
                        }
                    }
                    else
                    {
                        pts.Add(wp2);
                    }

                    updateBounds(wp1.X, wp1.Y);
                    updateBounds(wp2.X, wp2.Y);
                }

                model.Polylines.Add(new CadPolylineDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    IsClosed = poly2d.IsClosed,
                    Points = pts
                });
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Polyline3D poly3d:
            {
                var verts = poly3d.Vertices.ToList();
                if (verts.Count < 2) break;
                var pts = new List<WorldPoint>(verts.Count);
                foreach (var v in verts)
                {
                    var wp = transform.Apply(v.Location.X, v.Location.Y);
                    pts.Add(wp);
                    updateBounds(wp.X, wp.Y);
                    model.SnapPoints.Add(new SnapCandidate(wp.X, wp.Y, SnapPointKind.Vertex, layerName));
                }

                model.Polylines.Add(new CadPolylineDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    IsClosed = poly3d.IsClosed,
                    Points = pts
                });
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Arc arc:
            {
                double start = arc.StartAngle;
                double end = arc.EndAngle;
                if (end <= start) end += Math.PI * 2.0;

                int steps = Math.Clamp((int)Math.Ceiling((end - start) / (Math.PI / 18.0)), 8, 64);
                var pts = new List<WorldPoint>(steps + 1);
                for (int i = 0; i <= steps; i++)
                {
                    double ang = start + (end - start) * i / steps;
                    double lx = arc.Center.X + arc.Radius * Math.Cos(ang);
                    double ly = arc.Center.Y + arc.Radius * Math.Sin(ang);
                    var wp = transform.Apply(lx, ly);
                    pts.Add(wp);
                    updateBounds(wp.X, wp.Y);
                }

                var centerW = transform.Apply(arc.Center.X, arc.Center.Y);
                model.SnapPoints.Add(new SnapCandidate(centerW.X, centerW.Y, SnapPointKind.Center, layerName));
                if (pts.Count > 0)
                {
                    model.SnapPoints.Add(new SnapCandidate(pts[0].X, pts[0].Y, SnapPointKind.Endpoint, layerName));
                    model.SnapPoints.Add(new SnapCandidate(pts[^1].X, pts[^1].Y, SnapPointKind.Endpoint, layerName));
                }

                model.Polylines.Add(new CadPolylineDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    IsClosed = false,
                    Points = pts
                });
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Circle circle:
            {
                var centerW = transform.Apply(circle.Center.X, circle.Center.Y);
                double scaleAvg = transform.ApproximateScale();
                double r = Math.Abs(circle.Radius * scaleAvg);

                model.Circles.Add(new CadCircleDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    CenterX = centerW.X,
                    CenterY = centerW.Y,
                    Radius = r
                });

                updateBounds(centerW.X - r, centerW.Y - r);
                updateBounds(centerW.X + r, centerW.Y + r);
                model.SnapPoints.Add(new SnapCandidate(centerW.X, centerW.Y, SnapPointKind.Center, layerName));

                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Ellipse ellipse:
                {
                    var vertices = ellipse.PolygonalVertexes(48);
                    if (vertices.Count < 2) break;

                    var pts = new List<WorldPoint>(vertices.Count);
                    foreach (var v in vertices)
                    {
                        var wp = transform.Apply(v.X, v.Y);
                        pts.Add(wp);
                        updateBounds(wp.X, wp.Y);
                    }

                    model.Polylines.Add(new CadPolylineDrawable
                    {
                        LayerName = layerName,
                        Color = color,
                        IsDefaultWhite = isDefaultWhite,
                        IsClosed = ellipse.IsFullEllipse,
                        Points = pts
                    });
                    layerItem.EntityCount++;
                    model.TotalEntityCount++;
                    break;
                }

            case Spline spline:
            {
                var sourcePts = spline.FitPoints.Count >= 2 ? spline.FitPoints : spline.ControlPoints;
                if (sourcePts.Count < 2) break;
                var pts = new List<WorldPoint>(sourcePts.Count);
                foreach (var p in sourcePts)
                {
                    var wp = transform.Apply(p.X, p.Y);
                    pts.Add(wp);
                    updateBounds(wp.X, wp.Y);
                }
                model.Polylines.Add(new CadPolylineDrawable
                {





                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    IsClosed = false,
                    Points = pts
                });
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Solid solid:
            {
                var p1 = transform.Apply(solid.FirstCorner.X, solid.FirstCorner.Y);
                var p2 = transform.Apply(solid.SecondCorner.X, solid.SecondCorner.Y);
                var p3 = transform.Apply(solid.FourthCorner.X, solid.FourthCorner.Y);
                var p4 = transform.Apply(solid.ThirdCorner.X, solid.ThirdCorner.Y);
                model.Polylines.Add(new CadPolylineDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    IsClosed = true,
                    IsFilled = true,
                    Points = new List<WorldPoint> { p1, p2, p3, p4 }
                });
                updateBounds(p1.X, p1.Y);
                updateBounds(p2.X, p2.Y);
                updateBounds(p3.X, p3.Y);
                updateBounds(p4.X, p4.Y);
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Hatch hatch:
                {
                    if (!hatch.IsSolid) break; // هاشورهای الگودار غیرتُپُر به‌صورت سطح یکدست پر نشوند

                    var hatchDrawable = new CadHatchDrawable
                    {
                        LayerName = layerName,
                        Color = color,
                        IsDefaultWhite = isDefaultWhite,
                        IsSolid = hatch.IsSolid
                    };

                    foreach (var path in hatch.Paths)
                    {
                        var pts = new List<WorldPoint>();
                        foreach (var edge in path.Edges)
                        {
                            switch (edge)
                            {
                                case Hatch.BoundaryPath.Polyline hPoly when hPoly.Vertices.Count >= 2:
                                    for (int i = 0; i < hPoly.Vertices.Count; i++)
                                    {
                                        var v1 = hPoly.Vertices[i];
                                        var v2 = hPoly.Vertices[(i + 1) % hPoly.Vertices.Count];
                                        var wp1 = transform.Apply(v1.X, v1.Y);
                                        if (i == 0) pts.Add(wp1);

                                        double bulge = v1.Z;
 
                                        if (Math.Abs(bulge) > 1e-3 && Math.Abs(bulge) <= 1.5 && (hPoly.IsClosed || i < hPoly.Vertices.Count - 1))
                                            {
                                            var arcPts = TessellateBulge(v1.X, v1.Y, v2.X, v2.Y, bulge, 12);
                                            for (int k = 1; k < arcPts.Count; k++)
                                                pts.Add(transform.Apply(arcPts[k].X, arcPts[k].Y));
                                        }
                                        else if (i < hPoly.Vertices.Count - 1)
                                        {
                                            pts.Add(transform.Apply(v2.X, v2.Y));
                                        }
                                    }
                                    break;

                                case Hatch.BoundaryPath.Line hLine:
                                    if (pts.Count == 0) pts.Add(transform.Apply(hLine.Start.X, hLine.Start.Y));
                                    pts.Add(transform.Apply(hLine.End.X, hLine.End.Y));
                                    break;

                                case Hatch.BoundaryPath.Arc hArc when hArc.Radius > 1e-6 && hArc.Radius < 50000:
                                    double sAng = hArc.StartAngle;
                                    double eAng = hArc.EndAngle;
                                    double delta = eAng - sAng;
                                    while (delta <= 0) delta += Math.PI * 2.0;
                                    while (delta > Math.PI * 2.0 + 1e-6) delta -= Math.PI * 2.0;

                                    // در استاندارد DWG برای قوس‌های ساعتگرد، زاویه‌ها قرینه (2π - angle) ذخیره می‌شوند
                                    double trueStart = hArc.CounterClockWise ? sAng : (Math.PI * 2.0 - sAng);
                                    double signedSweep = hArc.CounterClockWise ? delta : -delta;

                                    int arcSteps = Math.Clamp((int)Math.Ceiling(delta / (Math.PI / 12.0)), 6, 36);
                                    var arcLoopPts = new List<WorldPoint>(arcSteps + 1);
                                    for (int k = 0; k <= arcSteps; k++)
                                    {
                                        double a = trueStart + signedSweep * k / arcSteps;
                                        arcLoopPts.Add(transform.Apply(
                                            hArc.Center.X + hArc.Radius * Math.Cos(a),
                                            hArc.Center.Y + hArc.Radius * Math.Sin(a)));
                                    }

                                    // اگر انتهای قوس به نقطه قبلی مسیر نزدیک‌تر بود، جهت قوس را معکوس می‌کنیم تا مرز پیوسته بماند
                                    if (pts.Count > 0 && arcLoopPts.Count >= 2)
                                    {
                                        var prev = pts[^1];
                                        double dStart = (arcLoopPts[0].X - prev.X) * (arcLoopPts[0].X - prev.X) + (arcLoopPts[0].Y - prev.Y) * (arcLoopPts[0].Y - prev.Y);
                                        double dEnd = (arcLoopPts[^1].X - prev.X) * (arcLoopPts[^1].X - prev.X) + (arcLoopPts[^1].Y - prev.Y) * (arcLoopPts[^1].Y - prev.Y);
                                        if (dEnd < dStart) arcLoopPts.Reverse();
                                    }

                                    pts.AddRange(arcLoopPts);
                                    break;

                                case Hatch.BoundaryPath.Spline hSpline:
                                    foreach (var cp in hSpline.ControlPoints)
                                        pts.Add(transform.Apply(cp.X, cp.Y));
                                    break;
                            }
                        }

                        if (pts.Count >= 3)
                            hatchDrawable.Loops.Add(pts);
                    }

                    if (hatchDrawable.Loops.Count > 0)
                    {
                        model.Hatches.Add(hatchDrawable);
                        layerItem.EntityCount++;
                        model.TotalEntityCount++;
                    }
                    break;
                }


            case Point cadPoint:
            {
                var wp = transform.Apply(cadPoint.Location.X, cadPoint.Location.Y);
                updateBounds(wp.X, wp.Y);
                model.SnapPoints.Add(new SnapCandidate(wp.X, wp.Y, SnapPointKind.Endpoint, layerName));
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case TextEntity textEntity:
            {
                if (string.IsNullOrWhiteSpace(textEntity.Value)) break;
                var wp = transform.Apply(textEntity.InsertPoint.X, textEntity.InsertPoint.Y);
                double h = Math.Max(0.2, Math.Abs(textEntity.Height * transform.ApproximateScale()));
                model.Texts.Add(new CadTextDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    X = wp.X,
                    Y = wp.Y,
                    Height = h,
                    RotationDegrees = textEntity.Rotation * 180.0 / Math.PI,
                    RawText = textEntity.Value,
                    CleanedText = PersianTextHelper.CleanCadText(textEntity.Value, fixLegacyPersianShx)
                });
                updateBounds(wp.X, wp.Y);
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case MText mText:
            {
                if (string.IsNullOrWhiteSpace(mText.Value)) break;
                var wp = transform.Apply(mText.InsertPoint.X, mText.InsertPoint.Y);
                double h = Math.Max(0.2, Math.Abs(mText.Height * transform.ApproximateScale()));
                model.Texts.Add(new CadTextDrawable
                {
                    LayerName = layerName,
                    Color = color,
                    IsDefaultWhite = isDefaultWhite,
                    X = wp.X,
                    Y = wp.Y,
                    Height = h,
                    RotationDegrees = mText.Rotation * 180.0 / Math.PI,
                    RawText = mText.Value,
                    CleanedText = PersianTextHelper.CleanCadText(mText.Value, fixLegacyPersianShx)
                });
                updateBounds(wp.X, wp.Y);
                layerItem.EntityCount++;
                model.TotalEntityCount++;
                break;
            }

            case Insert insert when depth < MaxBlockRecursionDepth && insert.Block != null:
            {
                var localTransform = Matrix2D.CreateTransform(
                    insert.InsertPoint.X, insert.InsertPoint.Y,
                    insert.XScale == 0 ? 1.0 : insert.XScale,
                    insert.YScale == 0 ? 1.0 : insert.YScale,
                    insert.Rotation);
                var combined = transform.Multiply(localTransform);

                foreach (Entity child in insert.Block.Entities)
                {
                    ExtractEntity(child, combined, layerName, color, depth + 1, model, layerMap, fixLegacyPersianShx, updateBounds);
                }
                break;
            }
        }
    }

    /// <summary>خروجی نقاط، محدوده، برچسب گوشه‌ها و طول اضلاع به فرمت استاندارد DXF برای باز کردن در اتوکد</summary>
    public static void ExportPointsToDxf(string filePath, IReadOnlyList<SurveyPoint> points, PolygonMetrics metrics, bool useGroundDistances)
    {
        var doc = new CadDocument();

        var layerBoundary = new Layer("UTM_BOUNDARY") { Color = new Color(1) }; // Red
        var layerPoints = new Layer("UTM_POINTS") { Color = new Color(2) };     // Yellow
        var layerLabels = new Layer("UTM_LABELS") { Color = new Color(3) };     // Green
        var layerDims = new Layer("UTM_DISTANCES") { Color = new Color(4) };    // Cyan

        doc.Layers.Add(layerBoundary);
        doc.Layers.Add(layerPoints);
        doc.Layers.Add(layerLabels);
        doc.Layers.Add(layerDims);

        // اندازه متن متناسب با ابعاد محدوده
        double span = 20.0;
        if (points.Count >= 2)
        {
            double dx = points.Max(p => p.Easting) - points.Min(p => p.Easting);
            double dy = points.Max(p => p.Northing) - points.Min(p => p.Northing);
            span = Math.Max(5.0, Math.Max(dx, dy));
        }
        double textHeight = Math.Clamp(span * 0.025, 0.4, 25.0);

        // 1. چندضلعی محدوده
        if (points.Count >= 2)
        {
            var poly = new LwPolyline
            {
                Layer = layerBoundary,
                IsClosed = points.Count >= 3
            };
            foreach (var pt in points)
            {
                poly.Vertices.Add(new LwPolyline.Vertex(new XY(pt.Easting, pt.Northing)));
            }
            doc.Entities.Add(poly);
        }

        // 2. نقاط و برچسب گوشه‌ها
        foreach (var pt in points)
        {
            doc.Entities.Add(new Point(new XYZ(pt.Easting, pt.Northing, 0)) { Layer = layerPoints });
            doc.Entities.Add(new Circle
            {
                Layer = layerPoints,
                Center = new XYZ(pt.Easting, pt.Northing, 0),
                Radius = textHeight * 0.35
            });
            doc.Entities.Add(new TextEntity
            {
                Layer = layerLabels,
                Value = pt.Label,
                InsertPoint = new XYZ(pt.Easting + textHeight * 0.5, pt.Northing + textHeight * 0.5, 0),
                Height = textHeight
            });
        }

        // 3. طول اضلاع روی وسط هر ضلع
        foreach (var edge in metrics.Edges)
        {
            double len = useGroundDistances ? edge.GroundLengthMeters : edge.PlanarLengthMeters;
            doc.Entities.Add(new TextEntity
            {
                Layer = layerDims,
                Value = $"{len:F2} m",
                InsertPoint = new XYZ(edge.MidX, edge.MidY, 0),
                Height = textHeight * 0.75
            });
        }

        // 4. مساحت در مرکز محدوده
        if (metrics.IsPolygonClosed)
        {
            double area = useGroundDistances ? metrics.GroundAreaSqMeters : metrics.PlanarAreaSqMeters;
            doc.Entities.Add(new TextEntity
            {
                Layer = layerDims,
                Value = $"Area = {area:N2} m2",
                InsertPoint = new XYZ(metrics.CentroidX, metrics.CentroidY, 0),
                Height = textHeight * 0.9
            });
        }

        DxfWriter.Write(filePath, doc, binary: false);
    }

    private static List<WorldPoint> TessellateBulge(double x1, double y1, double x2, double y2, double bulge, int segments)
    {
        var result = new List<WorldPoint> { new(x1, y1) };
        double dx = x2 - x1;
        double dy = y2 - y1;
        double chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-9) return result;

        double theta = 4.0 * Math.Atan(bulge);
        double radius = chord / (2.0 * Math.Sin(Math.Abs(theta) / 2.0));
        double midX = (x1 + x2) * 0.5;
        double midY = (y1 + y2) * 0.5;
        double h = Math.Sqrt(Math.Max(0, radius * radius - (chord * chord) / 4.0));

        double nx = -dy / chord;
        double ny = dx / chord;
        double sign = (bulge > 0 ? 1.0 : -1.0) * (Math.Abs(theta) > Math.PI ? -1.0 : 1.0);

        double cx = midX + sign * h * nx;
        double cy = midY + sign * h * ny;

        double startAng = Math.Atan2(y1 - cy, x1 - cx);
        for (int i = 1; i <= segments; i++)
        {
            double a = startAng + theta * i / segments;
            result.Add(new WorldPoint(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
        }
        return result;
    }

    private static RgbColor ResolveEntityColor(Entity entity, RgbColor layerColor, RgbColor? parentBlockColor, out bool isDefaultWhite)
    {
        var c = entity.Color;
        if (c.IsByLayer)
        {
            isDefaultWhite = layerColor.R == 255 && layerColor.G == 255 && layerColor.B == 255;
            return layerColor;
        }
        if (c.IsByBlock && parentBlockColor.HasValue)
        {
            var pc = parentBlockColor.Value;
            isDefaultWhite = pc.R == 255 && pc.G == 255 && pc.B == 255;
            return pc;
        }
        return ResolveAciOrTrueColor(c, out isDefaultWhite);
    }

    private static RgbColor ResolveAciOrTrueColor(Color c, out bool isDefaultWhite)
    {
        isDefaultWhite = false;
        if (c.IsTrueColor)
        {
            return new RgbColor(c.R, c.G, c.B);
        }

        short idx = c.Index;
        if (idx == 7 || idx == 0 || idx == 256)
        {
            isDefaultWhite = true;
            return new RgbColor(255, 255, 255);
        }

        return AciToRgb(idx);
    }

    private static RgbColor AciToRgb(short index)
    {
        return index switch
        {
            1 => new RgbColor(255, 0, 0),       // Red
            2 => new RgbColor(255, 255, 0),     // Yellow
            3 => new RgbColor(0, 255, 0),       // Green
            4 => new RgbColor(0, 255, 255),     // Cyan
            5 => new RgbColor(0, 0, 255),       // Blue
            6 => new RgbColor(255, 0, 255),     // Magenta
            7 => new RgbColor(255, 255, 255),   // White/Black
            8 => new RgbColor(128, 128, 128),   // Dark Gray
            9 => new RgbColor(192, 192, 192),   // Light Gray
            >= 250 and <= 255 => new RgbColor(
                (byte)(51 + (index - 250) * 40),
                (byte)(51 + (index - 250) * 40),
                (byte)(51 + (index - 250) * 40)),
            _ => HsvToRgb(((index - 10) / 10) * 15.0, (index % 2 == 0) ? 1.0 : 0.55,
                          ((index % 10) / 2) switch { 0 => 1.0, 1 => 0.8, 2 => 0.65, 3 => 0.5, _ => 0.35 })
        };
    }

    private static RgbColor HsvToRgb(double hueDeg, double sat, double val)
    {
        double h = ((hueDeg % 360.0) + 360.0) % 360.0;
        double c = val * sat;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = val - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new RgbColor((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private readonly struct Matrix2D
    {
        public readonly double M11, M12, M21, M22, Tx, Ty;

        public Matrix2D(double m11, double m12, double m21, double m22, double tx, double ty)
        {
            M11 = m11; M12 = m12; M21 = m21; M22 = m22; Tx = tx; Ty = ty;
        }

        public static Matrix2D Identity => new(1, 0, 0, 1, 0, 0);

        public static Matrix2D CreateTransform(double tx, double ty, double sx, double sy, double rotRad)
        {
            double cos = Math.Cos(rotRad);
            double sin = Math.Sin(rotRad);
            return new Matrix2D(cos * sx, -sin * sy, sin * sx, cos * sy, tx, ty);
        }

        public Matrix2D Multiply(Matrix2D b) => new(
            M11 * b.M11 + M12 * b.M21,
            M11 * b.M12 + M12 * b.M22,
            M21 * b.M11 + M22 * b.M21,
            M21 * b.M12 + M22 * b.M22,
            M11 * b.Tx + M12 * b.Ty + Tx,
            M21 * b.Tx + M22 * b.Ty + Ty);

        public WorldPoint Apply(double x, double y) => new(M11 * x + M12 * y + Tx, M21 * x + M22 * y + Ty);

        public double ApproximateScale()
        {
            double sx = Math.Sqrt(M11 * M11 + M21 * M21);
            double sy = Math.Sqrt(M12 * M12 + M22 * M22);
            return (sx + sy) * 0.5;
        }
    }
}
