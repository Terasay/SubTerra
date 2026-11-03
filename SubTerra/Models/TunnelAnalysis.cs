using System.Numerics;
using System.Text.Json.Serialization;

namespace SubTerra.Models;

public enum ProjectAnalysisMode { Room, Tunnel }

/// <summary>Section dimensions are in metres. Axis must lie inside a single passage.</summary>
public sealed record TunnelAnalysisOptions(double SectionSpacing = 2, double SliceThickness = .3,
    double MaximumRadius = 8, int AngularBins = 72, int MinimumSectionPoints = 24,
    int MaximumSamples = 500_000, double MaximumContourGap = .75)
{
    public void Validate()
    {
        if (!double.IsFinite(SectionSpacing) || SectionSpacing is < .2 or > 20 ||
            !double.IsFinite(SliceThickness) || SliceThickness is < .02 or > 2 || SliceThickness > SectionSpacing ||
            !double.IsFinite(MaximumRadius) || MaximumRadius is < .25 or > 50 || AngularBins is < 24 or > 180 ||
            MinimumSectionPoints is < 12 or > 10_000 || MaximumSamples is < 1_000 or > 1_000_000 ||
            !double.IsFinite(MaximumContourGap) || MaximumContourGap is < .02 or > 5)
            throw new ArgumentOutOfRangeException(nameof(TunnelAnalysisOptions),
                "Шаг сечений: 0,2–20 м; толщина: 0,02–2 м и не больше шага; радиус: 0,25–50 м; разрыв контура: 0,02–5 м.");
    }
}

/// <summary>An observed point in the plane normal to the tunnel axis. Missing bins remain missing.</summary>
public sealed record TunnelContourPoint(double X, double Y, int BinIndex, int SupportCount);

public sealed record TunnelSection(int Id, double Chainage, InspectionPoint Center, InspectionPoint Forward,
    InspectionPoint Right, InspectionPoint Up, TunnelContourPoint[] Contour, int PointCount,
    int CoveredBins, int AngularBins, bool IsClosed, double Width, double Height, double? AreaSquareMeters)
{
    [JsonIgnore] public double Coverage => AngularBins > 0 ? (double)CoveredBins / AngularBins : 0;
    [JsonIgnore] public double SlopeDegrees => Math.Atan2(Forward.Z, Math.Sqrt((double)Forward.X * Forward.X + (double)Forward.Y * Forward.Y)) * 180 / Math.PI;
    [JsonIgnore] public string Title => $"{Chainage:F1} м · {(IsClosed ? "замкнуто" : "неполное")}";
    [JsonIgnore] public string Details => $"Наблюдаемые габариты: {Width:F2} × {Height:F2} м · покрытие {Coverage:P0} · {PointCount:N0} точек" +
        (AreaSquareMeters is { } area ? $" · площадь ≈ {area:F2} м²" : " · площадь не определена");

    public bool ConnectsToNext(int contourIndex, double maximumGap)
    {
        if (Contour is null || Contour.Length < 2 || contourIndex < 0 || contourIndex >= Contour.Length || AngularBins < 1) return false;
        var a = Contour[contourIndex]; var b = Contour[(contourIndex + 1) % Contour.Length];
        return a is not null && b is not null && (a.BinIndex + 1) % AngularBins == b.BinIndex &&
            Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) <= maximumGap * maximumGap;
    }

    public Vector3 WorldPoint(TunnelContourPoint point) => Center.Position + Right.Position * (float)point.X + Up.Position * (float)point.Y;
}

/// <summary>Manual axis and bounded measured sections. RGB never contributes to geometry analysis.</summary>
public sealed record TunnelAnalysisDocument(int Version, InspectionPoint[] Axis, TunnelAnalysisOptions Options,
    TunnelSection[] Sections, StructureInputStamp[] Inputs, DateTimeOffset CreatedAtUtc,
    int SourcePointCount, int SampleCount, bool IsStale = false)
{
    [JsonIgnore] public double AxisLength
    {
        get
        {
            double length = 0;
            if (Axis is null) return length;
            for (var i = 1; i < Axis.Length; i++) length += Vector3.Distance(Axis[i - 1].Position, Axis[i].Position);
            return length;
        }
    }
    [JsonIgnore] public bool HasResults => Sections is { Length: > 0 };
}
