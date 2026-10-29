using System.Numerics;
using SubTerra.Models;

namespace SubTerra.Simulation;

public static class CaveFactory
{
    public static Vector3 Center(string scenarioId, float t) => scenarioId switch
    {
        "mine" => new(-30 + 60 * t, 2.5f * MathF.Sin(t * 7), .8f * MathF.Sin(t * 5)),
        "lava" => new(-28 + 56 * t, 8 * MathF.Sin(t * 7 - 1), 1.2f * MathF.Sin(t * 9)),
        _ => new(-34 + 68 * t, 6 * MathF.Sin(t * 6.5f) + 2 * MathF.Sin(t * 14),
            .8f * MathF.Sin(t * 7))
    };

    public static (Vector3 Forward, Vector3 Right, Vector3 Up) Basis(string scenarioId, float t)
    {
        var forward = Vector3.Normalize(Center(scenarioId, Math.Min(1, t + .002f)) -
                                        Center(scenarioId, Math.Max(0, t - .002f)));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        return (forward, right, Vector3.Normalize(Vector3.Cross(right, forward)));
    }

    public static SensorPose Pose(string scenarioId, float t)
    {
        var (forward, right, up) = Basis(scenarioId, t);
        var basis = new Matrix4x4(right.X, right.Y, right.Z, 0,
            forward.X, forward.Y, forward.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        return new(Center(scenarioId, t), Quaternion.CreateFromRotationMatrix(basis));
    }

    public static CaveScene Create(ScenarioDefinition scenario, int sceneVersion = SceneVersions.Current) => sceneVersion switch
    {
        SceneVersions.Legacy => LegacyCaveMeshBuilder.Build(scenario),
        SceneVersions.Current => CaveMeshBuilder.Build(scenario),
        _ => throw new ArgumentOutOfRangeException(nameof(sceneVersion), "Неизвестная версия виртуальной сцены.")
    };
}

/// <summary>A synthetic test building, not measurements or neural-network output.</summary>
public static class CollegeBuildingFactory
{
    public const int FloorCount = 3;
    public const float FloorSpacing = 3.6f, RoomHeight = 3.2f;
    public const float Width = 18, Depth = 12;

    public static ScanProject Create(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var objects = new List<ScanObject>();
        for (var floor = 0; floor < FloorCount; floor++)
        {
            var mesh = new TestMesh(token, floor);
            var z = floor * FloorSpacing;
            mesh.Quad(new(0, 0, z), new(Width, 0, 0), new(0, Depth, 0));
            mesh.Quad(new(0, 0, z + RoomHeight), new(0, Depth, 0), new(Width, 0, 0));
            mesh.Quad(new(0, 0, z), new(0, Depth, 0), new(0, 0, RoomHeight));
            mesh.Quad(new(0, 0, z), new(Width, 0, 0), new(0, 0, RoomHeight));
            mesh.Quad(new(0, Depth, z), new(0, 0, RoomHeight), new(Width, 0, 0));
            // The east exit opens onto a shared dog-leg staircase.
            mesh.Quad(new(Width, 0, z), new(0, 5.3f, 0), new(0, 0, RoomHeight));
            mesh.Quad(new(Width, 6.7f, z), new(0, 5.3f, 0), new(0, 0, RoomHeight));
            mesh.Quad(new(Width, 5.3f, z + 2.2f), new(0, 1.4f, 0), new(0, 0, RoomHeight - 2.2f));
            // Six 6 x 4.5 m classrooms and a 3 m wide corridor on each floor.
            foreach (var y in new[] { 4.5f, 7.5f })
                for (var room = 0; room < 3; room++)
                {
                    var x = room * 6f;
                    mesh.Quad(new(x, y, z), new(2.5f, 0, 0), new(0, 0, RoomHeight));
                    mesh.Quad(new(x + 3.5f, y, z), new(2.5f, 0, 0), new(0, 0, RoomHeight));
                    mesh.Quad(new(x + 2.5f, y, z + 2.1f), new(1, 0, 0), new(0, 0, RoomHeight - 2.1f));
                    // A 1 x 2.1 m door leaf opened into the classroom, with a visible thickness.
                    var along = new Vector3(.5f, y < 6 ? -.8660254f : .8660254f, 0);
                    var across = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitZ)) * .04f;
                    mesh.Box(new(x + 2.5f, y, z), along, across, new(0, 0, 2.1f), 0xA28E78);
                }
            foreach (var x in new[] { 6f, 12f })
            {
                mesh.Quad(new(x, 0, z), new(0, 4.5f, 0), new(0, 0, RoomHeight));
                mesh.Quad(new(x, 7.5f, z), new(0, 4.5f, 0), new(0, 0, RoomHeight));
            }
            objects.Add(mesh.Object($"Синтетический корпус · этаж {floor + 1}", $"college-synthetic-floor-{floor + 1}.ply"));
        }
        var stairs = new TestMesh(token, 73);
        var roof = (FloorCount - 1) * FloorSpacing + RoomHeight;
        stairs.Quad(new(22, 4, 0), new(0, 4, 0), new(0, 0, roof));
        stairs.Quad(new(18, 4, 0), new(4, 0, 0), new(0, 0, roof));
        stairs.Quad(new(18, 8, 0), new(0, 0, roof), new(4, 0, 0));
        stairs.Quad(new(18, 4, roof), new(0, 4, 0), new(4, 0, 0));
        for (var floor = 0; floor < FloorCount; floor++)
            stairs.Quad(new(18, 4, floor * FloorSpacing), new(.8f, 0, 0), new(0, 4, 0));
        for (var floor = 0; floor < FloorCount - 1; floor++)
        {
            var z = floor * FloorSpacing;
            stairs.Quad(new(21.2f, 4, z + 1.8f), new(.8f, 0, 0), new(0, 4, 0));
            for (var step = 0; step < 10; step++)
            {
                var h = z + (step + 1) * .18f;
                var x = 18.8f + step * .24f;
                stairs.Quad(new(x, 4, h), new(.24f, 0, 0), new(0, 1.8f, 0));
                stairs.Quad(new(x, 4, h - .18f), new(0, 1.8f, 0), new(0, 0, .18f));
                x = 21.2f - (step + 1) * .24f; h += 1.8f;
                stairs.Quad(new(x, 6.2f, h), new(.24f, 0, 0), new(0, 1.8f, 0));
                stairs.Quad(new(x + .24f, 6.2f, h - .18f), new(0, 0, .18f), new(0, 1.8f, 0));
            }
        }
        objects.Add(stairs.Object("Синтетический корпус · лестница", "college-synthetic-stairs.ply"));
        var project = new ScanProject(objects.ToArray(), objects[0].Id, objects[0].Source.Metadata);
        project.Validate(token);
        return project;
    }

    private sealed class TestMesh(CancellationToken token, int seed)
    {
        private readonly List<ScanPoint> _points = [];
        private readonly List<uint> _colors = [];
        private readonly List<int> _triangles = [];
        private readonly Random _random = new(seed);

        public void Quad(Vector3 origin, Vector3 u, Vector3 v, uint color = 0xACB6C0)
        {
            token.ThrowIfCancellationRequested();
            var columns = Math.Max(1, (int)Math.Ceiling(u.Length() / .18));
            var rows = Math.Max(1, (int)Math.Ceiling(v.Length() / .18));
            var offset = _points.Count;
            var normal = Vector3.Normalize(Vector3.Cross(u, v));
            for (var row = 0; row <= rows; row++)
                for (var column = 0; column <= columns; column++)
                {
                    token.ThrowIfCancellationRequested();
                    var p = origin + u * (column / (float)columns) + v * (row / (float)rows);
                    // Keep seams exact; small interior noise exercises tolerance without encoding semantic labels.
                    if (row > 0 && row < rows && column > 0 && column < columns)
                        p += normal * (float)((_random.NextDouble() - .5) * .003);
                    _points.Add(new(p.X, p.Y, p.Z, .8f)); _colors.Add(color);
                    if (row == rows || column == columns) continue;
                    var a = offset + row * (columns + 1) + column;
                    _triangles.AddRange([a, a + 1, a + columns + 1, a + 1, a + columns + 2, a + columns + 1]);
                }
        }
        public void Box(Vector3 o, Vector3 u, Vector3 v, Vector3 w, uint color)
        {
            Quad(o, u, w, color); Quad(o + v, w, u, color);
            Quad(o, v, u, color); Quad(o + w, u, v, color);
            Quad(o, w, v, color); Quad(o + u, v, w, color);
        }
        public ScanObject Object(string name, string fileName) => new(Guid.NewGuid(), name, new()
        {
            Metadata = new(fileName, ScanUnit.Meters, ScanUpAxis.Z, 0, 0, 0) { IsSynthetic = true },
            Points = _points.ToArray(), Colors = _colors.ToArray(), Triangles = _triangles.ToArray()
        });
    }
}
