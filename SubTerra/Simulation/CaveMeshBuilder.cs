using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Simulation;

/// <summary>Version 3: asymmetric erosion, broad rock shelves and a continuous walkable floor.</summary>
internal static class CaveMeshBuilder
{
    public static CaveScene Build(ScenarioDefinition scenario)
    {
        const int rings = 321, sides = 128, stride = sides + 1;
        var positions = new List<Vector3>(rings * stride + 6000);
        var uvs = new List<Vector2>();
        var times = new List<float>();
        var angles = new List<float>();
        var floorVertices = new List<bool>();
        var normalSeams = new List<(int A, int B)>();
        var indices = new List<int>();
        var route = new Vector3[rings];
        var distance = 0f;
        for (var r = 0; r < rings; r++)
        {
            var t = r / (float)(rings - 1);
            route[r] = CaveFactory.Center(scenario.Id, t);
            if (r > 0) distance += Vector3.Distance(route[r - 1], route[r]);
            normalSeams.Add((r * stride, r * stride + sides));
            for (var s = 0; s <= sides; s++)
            {
                // Duplicate the seam exactly; no tiny sin(2*pi) geometry discontinuity.
                var a = s == sides ? 0 : MathF.Tau * s / sides;
                var point = ShellPoint(scenario.Id, t, a);
                positions.Add(point);
                var floor = MathF.Sin(a) < -.57f;
                // Fixed angular scale avoids shearing the texture as a hall widens.
                // The sediment rendering layer supplies its own world XY mapping.
                uvs.Add(new(distance / 3.2f, s * 6f / sides));
                times.Add(t); angles.Add(a); floorVertices.Add(floor);
                if (r == rings - 1 || s == sides) continue;
                var i = r * stride + s;
                indices.AddRange([i, i + 1, i + stride, i + 1, i + stride + 1, i + stride]);
            }
        }

        // Keep the centre corridor free. All formations belong to the scan/collision
        // mesh, not a decorative layer that a person or a laser could pass through.
        for (var k = 0; k < 27; k++)
        {
            var t = .08f + k * .032f;
            var (width, _) = Dimensions(scenario.Id, t);
            var (_, right, up) = CaveFactory.Basis(scenario.Id, t);
            var side = k % 2 == 0 ? 1 : -1;
            var center = CaveFactory.Center(scenario.Id, t);
            var lateral = width * (.70f + .07f * MathF.Sin(k * 2.17f)) * side;
            var ground = center + right * lateral;
            ground += up * Floor(scenario.Id, t, lateral, ground);
            Formation(ground - Vector3.UnitZ * .16f, .28f + .10f * (k % 4),
                .25f + .13f * (k % 3), t, false, k, true);
            if (scenario.Id == "mine" || k % 3 != 0) continue;
            Formation(ground - Vector3.UnitZ * .10f, .32f + .07f * (k % 3),
                1.0f + .35f * (k % 4), t, false, k + 31, false);
            var ceiling = ShellPoint(scenario.Id, t, MathF.PI / 2 + side * .32f);
            Formation(ceiling + Vector3.UnitZ * .12f, .28f + .04f * (k % 4),
                .8f + .27f * (k % 4), t, true, k + 61, false);
        }

        var normals = new Vector3[positions.Count];
        var kinds = new CaveSurfaceKind[indices.Count / 3];
        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
            var point = (positions[a] + positions[b] + positions[c]) / 3;
            var floor = floorVertices[a] && floorVertices[b] && floorVertices[c];
            var moisture = RockNoise.Fractal(point * .24f + new Vector3(7, 13, 3), 3);
            kinds[i / 3] = floor ? CaveSurfaceKind.Sediment :
                moisture > .12f ? CaveSurfaceKind.DampRock : CaveSurfaceKind.Rock;
        }
        foreach (var (a, b) in normalSeams)
        {
            normals[a] = normals[b] = normals[a] + normals[b];
        }
        for (var i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 1e-10f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
        return new CaveScene
        {
            Scenario = scenario, Vertices = positions.ToArray(), Normals = normals,
            TextureCoordinates = uvs.ToArray(), Indices = indices.ToArray(), SurfaceKinds = kinds,
            VertexProgress = times.ToArray(), VertexAngles = angles.ToArray(), Route = route,
            LengthMeters = distance,
            Markers =
            [
                Marker("Скальные ниши", "Асимметричные выступы, натёки и обломки у стен.", .24f),
                Marker("Большой зал", "Расширение прохода с высоким неровным сводом.", .43f),
                Marker("Дальний проход", "Сужение между залами и нависающие скальные полки.", .78f)
            ]
        };

        SceneMarker Marker(string title, string description, float t) =>
            new(title, description, "Геометрия", t, CaveFactory.Center(scenario.Id, t));

        void Formation(Vector3 origin, float radius, float height, float t, bool hanging, int seed, bool boulder)
        {
            const int count = 16, layers = 9;
            var start = positions.Count;
            for (var layer = 0; layer <= layers; layer++)
            {
                normalSeams.Add((start + layer * (count + 1), start + layer * (count + 1) + count));
                var f = layer / (float)layers;
                var size = boulder ? radius * MathF.Sqrt(Math.Max(.001f, 1 - f * f)) :
                    radius * MathF.Pow(1 - f, .72f) + .018f;
                for (var s = 0; s <= count; s++)
                {
                    var a = s == count ? 0 : MathF.Tau * s / count;
                    var wobble = 1 + .14f * MathF.Sin(a * 3 + seed) + .07f * MathF.Sin(a * 7 + f * 6 + seed);
                    var point = origin + new Vector3(MathF.Cos(a) * size * wobble + .16f * f * MathF.Sin(seed),
                        MathF.Sin(a) * size * wobble, height * f * (hanging ? -1 : 1));
                    positions.Add(point);
                    uvs.Add(new(s * MathF.Tau / count * radius / 3.2f, f * height / 3.2f));
                    times.Add(t); angles.Add(hanging ? MathF.PI / 2 : MathF.PI * 1.5f);
                    floorVertices.Add(false);
                    if (s == count || layer == layers) continue;
                    var i = start + layer * (count + 1) + s;
                    if (!hanging) indices.AddRange([i, i + 1, i + count + 1, i + 1, i + count + 2, i + count + 1]);
                    else indices.AddRange([i, i + count + 1, i + 1, i + 1, i + count + 1, i + count + 2]);
                }
            }
        }
    }

    private static Vector3 ShellPoint(string id, float t, float angle)
    {
        var center = CaveFactory.Center(id, t);
        var (_, right, up) = CaveFactory.Basis(id, t);
        var (width, height) = Dimensions(id, t);
        var cos = MathF.Cos(angle); var sin = MathF.Sin(angle);
        var preliminary = center + right * (cos * width) + up * (sin * height);
        var rough = RockNoise.Fractal(preliminary * .40f, 3) * .17f +
                    RockNoise.Fractal(preliminary * 1.35f, 2) * .045f;
        var aboveFloor = Smooth((sin + .64f) / .45f);
        var asymmetry = .10f * MathF.Sin(t * 19 + 1) * cos;
        var lobes = (.09f * MathF.Sin(angle * 3 + t * 13) + .055f * MathF.Sin(angle * 5 - t * 23)) * aboveFloor;
        // Local recesses and overhangs; angular windows produce features on one wall.
        var niche = .28f * Bell(t, .24f, .065f) * MathF.Pow(Math.Max(0, cos), 5)
                  + .22f * Bell(t, .61f, .05f) * MathF.Pow(Math.Max(0, -cos), 6);
        var shelf = -.15f * Bell(t, .77f, .045f) * MathF.Pow(Math.Max(0, MathF.Cos(angle - .45f)), 8);
        var profile = id == "mine" ? MathF.CopySign(MathF.Pow(MathF.Abs(cos), .72f), cos) : cos;
        // Fade erosion into the floor: high-frequency lateral offsets can fold a
        // floor quad even though each vertex has a valid height.
        var lateral = profile * width * (1 + asymmetry + aboveFloor * (rough + lobes + niche + shelf));
        var vertical = sin * height * (1 + rough * .65f + lobes * .75f);
        if (sin < -.57f) vertical = Floor(id, t, lateral, center + right * lateral);
        return center + right * lateral + up * vertical;
    }

    private static float Floor(string id, float t, float lateral, Vector3 world)
    {
        var (_, height) = Dimensions(id, t);
        return -height * .64f + RockNoise.Fractal(world * .62f, 3) * .10f +
               .035f * MathF.Sin(lateral * 1.7f + t * 13);
    }

    private static (float Width, float Height) Dimensions(string id, float t)
    {
        if (id == "mine") return (2.7f + .15f * MathF.Sin(t * 23), 3.15f + .17f * MathF.Sin(t * 17));
        var hall = Bell(t, .43f, .12f);
        var second = Bell(t, .73f, .075f);
        return id == "lava" ? (3.0f + 1.7f * hall + .35f * MathF.Sin(t * 23), 3.0f + .8f * second + .32f * MathF.Sin(t * 16)) :
            (3.1f + 3.5f * hall + 1.4f * second + .32f * MathF.Sin(t * 24),
             3.2f + 2.1f * hall + .85f * second + .20f * MathF.Sin(t * 17));
    }
    private static float Bell(float t, float center, float width) => MathF.Exp(-MathF.Pow((t - center) / width, 2));
    private static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
}
