using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Simulation;

// Frozen v2 geometry: saved measurements must keep the surface they were captured against.
internal static class LegacyCaveMeshBuilder
{
    public static CaveScene Build(ScenarioDefinition scenario)
    {
        const int rings = 301, sides = 144, stride = sides + 1;
        var positions = new List<Vector3>(rings * stride + 4000);
        var uvs = new List<Vector2>();
        var times = new List<float>();
        var angles = new List<float>();
        var indices = new List<int>();
        var route = new Vector3[rings];
        var distance = 0f;
        for (var r = 0; r < rings; r++)
        {
            var t = r / (float)(rings - 1);
            route[r] = CaveFactory.Center(scenario.Id, t);
            if (r > 0) distance += Vector3.Distance(route[r - 1], route[r]);
            var (_, right, up) = CaveFactory.Basis(scenario.Id, t);
            var (width, height) = Dimensions(scenario.Id, t);
            for (var s = 0; s <= sides; s++)
            {
                var a = MathF.Tau * s / sides;
                var preliminary = route[r] + right * (MathF.Cos(a) * width) + up * (MathF.Sin(a) * height);
                var rough = RockNoise.Fractal(preliminary * .47f) * .23f +
                            RockNoise.Fractal(preliminary * 1.4f, 2) * .055f;
                var strata = MathF.Sin(preliminary.Z * 6 + RockNoise.Noise(preliminary * .2f) * 2) * .035f;
                var lateral = MathF.Cos(a) * width * (1 + rough + strata);
                var vertical = MathF.Sin(a) * height * (1 + rough * .75f);
                if (MathF.Sin(a) < -.57f)
                    vertical = -height * .64f + RockNoise.Fractal(preliminary * .8f, 3) * .13f;
                positions.Add(route[r] + right * lateral + up * vertical);
                uvs.Add(new(distance / 3.2f, a * (width + height) * .5f / 3.2f));
                times.Add(t); angles.Add(a);
                if (r == rings - 1 || s == sides) continue;
                var i = r * stride + s;
                indices.AddRange([i, i + 1, i + stride, i + 1, i + stride + 1, i + stride]);
            }
        }
        // Formations are part of the same source geometry and participate in both scanning and collisions.
        if (scenario.Id != "mine")
        {
            for (var k = 0; k < 15; k++)
            {
                var t = .13f + k * .051f;
                var (width, height) = Dimensions(scenario.Id, t);
                var (_, right, _) = CaveFactory.Basis(scenario.Id, t);
                var side = k % 2 == 0 ? 1 : -1;
                var basePoint = CaveFactory.Center(scenario.Id, t) + right * (width * .72f * side);
                basePoint.Z -= height * .64f + .08f;
                Formation(basePoint, .5f + .15f * (k % 3), 1.0f + .44f * (k % 4), t, false, k);
                if (k % 2 != 0) continue;
                var ceiling = CaveFactory.Center(scenario.Id, t) + right * (width * .28f * side);
                ceiling.Z += height * .97f;
                Formation(ceiling, .34f + .1f * (k % 3), .8f + .33f * (k % 4), t, true, k + 19);
            }
        }
        var normals = new Vector3[positions.Count];
        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = indices[i]; var b = indices[i+1]; var c = indices[i+2];
            var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += n; normals[b] += n; normals[c] += n;
        }
        for (var r = 0; r < rings; r++)
        {
            var a = r * stride; var b = a + sides;
            normals[a] = normals[b] = normals[a] + normals[b];
        }
        for (var i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 1e-10f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
        return new CaveScene
        {
            SceneVersion = SceneVersions.Legacy, Scenario = scenario, Vertices = positions.ToArray(), Normals = normals,
            TextureCoordinates = uvs.ToArray(), Indices = indices.ToArray(),
            VertexProgress = times.ToArray(), VertexAngles = angles.ToArray(), Route = route,
            LengthMeters = distance,
            Markers =
            [
                Marker("Каменные образования", "Выступы и натёчные образования у стены прохода.", .24f),
                Marker("Большой зал", "Расширение пещеры с высоким сводом.", .43f),
                Marker("Дальний проход", "Переход из второго зала в узкий тоннель.", .78f)
            ]
        };

        SceneMarker Marker(string title, string description, float t) =>
            new(title, description, "Геометрия", t, CaveFactory.Center(scenario.Id, t));

        void Formation(Vector3 origin, float radius, float height, float t, bool hanging, int seed)
        {
            const int count = 18, layers = 8;
            var start = positions.Count;
            for (var layer = 0; layer <= layers; layer++)
            {
                var f = layer / (float)layers;
                var size = radius * MathF.Pow(1 - f, .72f) + .025f;
                for (var s = 0; s <= count; s++)
                {
                    var a = MathF.Tau * s / count;
                    var wobble = 1 + .13f * MathF.Sin(a * 5 + seed + f * 6);
                    var p = origin + new Vector3(MathF.Cos(a) * size * wobble + .12f * f * MathF.Sin(seed),
                        MathF.Sin(a) * size * wobble, height * f * (hanging ? -1 : 1));
                    positions.Add(p);
                    uvs.Add(new(a * radius / 3.2f, f * height / 3.2f));
                    times.Add(t); angles.Add(hanging ? MathF.PI / 2 : MathF.PI * 1.5f);
                    if (s == count || layer == layers) continue;
                    var i = start + layer * (count + 1) + s;
                    if (!hanging) indices.AddRange([i, i+1, i+count+1, i+1, i+count+2, i+count+1]);
                    else indices.AddRange([i, i+count+1, i+1, i+1, i+count+1, i+count+2]);
                }
            }
        }
    }

    private static (float Width, float Height) Dimensions(string id, float t)
    {
        var hall = MathF.Exp(-MathF.Pow((t - .43f) * 9, 2));
        var second = MathF.Exp(-MathF.Pow((t - .73f) * 14, 2));
        return id == "mine" ? (2.7f + .25f * MathF.Sin(t * 17), 3.1f) :
            (3.0f + 3.2f * hall + 1.5f * second + .3f * MathF.Sin(t * 27),
                3.2f + 1.9f * hall + .7f * second);
    }
}
