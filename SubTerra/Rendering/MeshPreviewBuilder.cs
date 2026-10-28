using System.Numerics;
using HelixToolkit;
using SubTerra.Models;

namespace SubTerra.Rendering;

/// <summary>Quadric edge collapse changes the display mesh; the project keeps the original geometry.</summary>
public static class MeshPreviewBuilder
{
    public static ImportedScan Build(ImportedScan scan, int triangleTarget, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(triangleTarget, 1);
        token.ThrowIfCancellationRequested();
        if (scan.Triangles.Length / 3 <= triangleTarget) return scan;
        var geometry = new HelixToolkit.Geometry.MeshGeometry3D
        {
            Positions = new Vector3Collection(scan.Points.Select(p => p.Position)),
            TriangleIndices = new IntCollection(scan.Triangles)
        };
        // Helix's QEM call is synchronous. It runs behind a single background preparation gate.
        // Cancellation is checked immediately before and after this bounded library call.
        var simplified = new HelixToolkit.Geometry.MeshSimplification(geometry).Simplify(triangleTarget, 7, false, false);
        token.ThrowIfCancellationRequested();
        if (simplified.TriangleIndices.Count == 0) return scan;
        var points = simplified.Positions.Select(p => new ScanPoint(p.X, p.Y, p.Z, 1)).ToArray();
        var colors = scan.Colors.Length == 0 ? [] : TransferColors(scan, simplified.Positions, token);
        return new() { Metadata = scan.Metadata, Points = points, Colors = colors, Triangles = simplified.TriangleIndices.ToArray() };
    }
    private static uint[] TransferColors(ImportedScan source, IList<Vector3> positions, CancellationToken token)
    {
        var bounds = source.Bounds;
        var size = Math.Max(.000001, (bounds.Maximum - bounds.Minimum).Length() / Math.Cbrt(Math.Max(1, source.Points.Length)));
        (long X, long Y, long Z) Key(Vector3 p) => ((long)Math.Floor(p.X / size), (long)Math.Floor(p.Y / size), (long)Math.Floor(p.Z / size));
        var cells = new Dictionary<(long X, long Y, long Z), List<int>>();
        for (var i = 0; i < source.Points.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var key = Key(source.Points[i].Position);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(i);
        }
        var colors = new uint[positions.Count];
        for (var i = 0; i < colors.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var p = positions[i]; var key = Key(p); var best = float.MaxValue; var nearest = 0;
            for (var radius = 1; radius <= 3 && best == float.MaxValue; radius++)
                for (var x = -radius; x <= radius; x++) for (var y = -radius; y <= radius; y++) for (var z = -radius; z <= radius; z++)
                {
                    if (!cells.TryGetValue((key.X + x, key.Y + y, key.Z + z), out var list)) continue;
                    foreach (var candidate in list)
                    {
                        var distance = Vector3.DistanceSquared(p, source.Points[candidate].Position);
                        if (distance < best) { best = distance; nearest = candidate; }
                    }
                }
            colors[i] = source.Colors[nearest];
        }
        return colors;
    }
}
