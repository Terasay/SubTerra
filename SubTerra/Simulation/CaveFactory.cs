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
