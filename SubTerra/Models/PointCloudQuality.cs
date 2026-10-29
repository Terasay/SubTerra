namespace SubTerra.Models;

public sealed record PointCloudQuality(string Name, int PointLimit)
{
    public override string ToString() => Name;
    public static IReadOnlyList<PointCloudQuality> All { get; } =
    [
        new("Экономно · до 100 тыс.", 100_000),
        new("Быстро · до 500 тыс.", 500_000),
        new("Баланс · до 1 млн", 1_000_000),
        new("Подробно · до 3 млн", 3_000_000),
        new("Все точки · до 5 млн", ImportedScan.MaximumPoints)
    ];
}
