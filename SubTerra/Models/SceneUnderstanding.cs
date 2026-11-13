using System.Text.Json.Serialization;

namespace SubTerra.Models;

// The first twenty classes follow the released ScanNet checkpoint's order, plus an unknown class.
public enum SceneClass
{
    Unknown, Wall, Floor, Cabinet, Bed, Chair, Sofa, Table, Door, Window, Bookshelf,
    Picture, Counter, Desk, Curtain, Refrigerator, ShowerCurtain, Toilet, Sink, Bathtub,
    OtherFurniture, Ceiling, Person
}

public sealed record ScenePoint(SourcePointReference Source, SceneClass SuggestedClass, float Score, int RegionId);
public sealed record SceneRegion(int Id, SceneClass SuggestedClass, int PointCount, SceneClass? ReviewedClass = null)
{
    [JsonIgnore] public SceneClass EffectiveClass => ReviewedClass ?? SuggestedClass;
    [JsonIgnore] public string Title => $"{Id} · {Name(EffectiveClass)} · {PointCount:N0} точек";
    [JsonIgnore] public string ReviewText => ReviewedClass.HasValue ? "Проверено оператором" : "Предположение ИИ";
    public static string Name(SceneClass kind) => kind switch
    {
        SceneClass.Wall => "Стена", SceneClass.Floor => "Пол", SceneClass.Cabinet => "Шкаф",
        SceneClass.Bed => "Кровать", SceneClass.Chair => "Стул", SceneClass.Sofa => "Диван",
        SceneClass.Table => "Стол", SceneClass.Door => "Дверь", SceneClass.Window => "Окно",
        SceneClass.Bookshelf => "Книжная полка", SceneClass.Picture => "Картина", SceneClass.Counter => "Стойка",
        SceneClass.Desk => "Рабочий стол", SceneClass.Curtain => "Занавеска", SceneClass.Refrigerator => "Холодильник",
        SceneClass.ShowerCurtain => "Шторка душа", SceneClass.Toilet => "Унитаз", SceneClass.Sink => "Раковина",
        SceneClass.Bathtub => "Ванна", SceneClass.OtherFurniture => "Другая мебель", SceneClass.Ceiling => "Потолок",
        SceneClass.Person => "Человек", _ => "Не определено"
    };
}

public sealed record SceneUnderstandingDocument(int Version, string Model, DateTimeOffset CreatedAt,
    StructureInputStamp[] Inputs, string ColorsHash, ScenePoint[] Points, SceneRegion[] Regions, bool IsStale = false);
