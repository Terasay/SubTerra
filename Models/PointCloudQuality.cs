namespace SubTerra.Models;

public sealed record PointCloudQuality(string Name, int PointLimit)
{
    public override string ToString() => Name;
    public static IReadOnlyList<PointCloudQuality> All { get; } =
    [
        new("Быстро · до 25 тыс.", 25_000),
        new("Баланс · до 60 тыс.", 60_000),
        new("Подробно · до 150 тыс.", 150_000)
    ];
}
