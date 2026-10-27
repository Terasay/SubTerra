using System.Globalization;
using System.Text;

namespace SubTerra.Diagnostics;

internal static class ReportMarkdown
{
    public static string Build(BenchmarkReport report)
    {
        var s = new StringBuilder();
        s.AppendLine("# Проверка нагрузки Subterra");
        s.AppendLine();
        s.AppendLine($"Измерение завершено {report.FinishedUtc:yyyy-MM-dd HH:mm:ss} UTC. Прогрев: {report.WarmupPasses} прохода; измеренных проходов: {report.Passes.Count}; полное время запуска: {F(report.ElapsedSeconds)} с.");
        s.AppendLine();
        s.AppendLine("## Условия");
        s.AppendLine();
        s.AppendLine($"- ОС: {report.OperatingSystem}.");
        s.AppendLine($"- Среда: {report.Runtime}, {report.Architecture}, сборка {report.Configuration}.");
        s.AppendLine($"- Процессор: {report.Cpu}; логических процессоров: {report.LogicalProcessors}.");
        s.AppendLine($"- Объём памяти, доступный GC по данным среды: {MiB(report.GcAvailableMemoryBytes)} МиБ.");
        s.AppendLine("- Три сценария чередуются; 180 кадров, 720 или 1280 лучей, дальность 20 м, период измерений 100 мс. Некоторые проходы начинаются с 35% маршрута.");
        s.AppendLine("- Все кадры обрабатываются последовательно без ожидания реального времени. Облака сравниваются без среза потолка; поверхность — со срезом, с разделением на сухой камень, гравий и влажный камень.");
        s.AppendLine("- Замер выполняется в обычной рабочей среде; фоновые приложения не закрываются.");
        s.AppendLine();
        s.AppendLine("## Результаты");
        s.AppendLine();
        s.AppendLine("| № | Сцена | Измерено / показано точек | Генерация, мс | Подготовка сцены, мс | Облако без лимита p95, мс | Облако 60 тыс. p95, мс | Три материала p95, мс | Добавление кадра + потоковый просмотр p95, мс | Managed после GC, МиБ | Private после GC, МиБ |");
        s.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var p in report.Passes)
            s.AppendLine($"| {p.Pass} | {p.Scenario} | {p.PointCount} / {p.DisplayedPoints} | {F(p.GenerationMs)} | {F(p.ScenePreparationMs)} | {F(p.FullCloud.P95Ms)} | {F(p.LimitedCloud.P95Ms)} | {F(p.Surface.P95Ms)} | {F(p.StreamingCloud.P95Ms)} | {MiB(p.MemoryAfterCollection.ManagedBytes)} | {MiB(p.MemoryAfterCollection.PrivateBytes)} |");
        s.AppendLine();
        var fullBytes = report.Passes.Sum(p => p.FullCloud.AllocatedBytes);
        var limitedBytes = report.Passes.Sum(p => p.LimitedCloud.AllocatedBytes);
        var last = report.Passes[^1].MemoryAfterCollection;
        s.AppendLine($"При подготовке облаков выделено {MiB(fullBytes)} МиБ без лимита и {MiB(limitedBytes)} МиБ с лимитом 60 тыс. Разница: {F((1 - limitedBytes / (double)Math.Max(1, fullBytes)) * 100)}%.");
        s.AppendLine();
        s.AppendLine($"После прогрева managed-память: {MiB(report.InitialMemory.ManagedBytes)} МиБ; после последнего прохода и GC: {MiB(last.ManagedBytes)} МиБ. Изменение: {MiB(last.ManagedBytes - report.InitialMemory.ManagedBytes)} МиБ.");
        s.AppendLine($"Private bytes: {MiB(report.InitialMemory.PrivateBytes)} → {MiB(last.PrivateBytes)} МиБ. Максимальный рабочий набор процесса: {MiB(report.Passes.Max(p => p.ProcessPeakWorkingSetBytes))} МиБ.");
        s.AppendLine();
        s.AppendLine("## Как понимать замер");
        s.AppendLine();
        s.AppendLine("p95 означает, что 95% операций подготовки укладываются в указанное время. Для обновления геометрии 10 раз в секунду бюджет CPU на один шаг составляет 100 мс. Эти числа относятся к генерации и подготовке данных HelixToolkit на CPU.");
        s.AppendLine();
        s.AppendLine("Сравниваются два режима одного текущего кода: полное облако и ограниченный просмотр. Это не замер предыдущей версии приложения. Стабильная выборка рассчитана для готового сеанса, поэтому в ранних кадрах число показанных точек может быть меньше выбранного лимита.");
        s.AppendLine();
        s.AppendLine("Дополнительно измерен потоковый путь приложения: кадры по одному добавляются в пустую геометрию, затем строится облако до 60 тыс. точек только из полученного префикса. Его p95 включает добавление точек и цветов, выборку и создание буфера просмотра. Входные кадры в этом CPU-тесте рассчитаны заранее; асинхронный источник, пауза и продолжение отдельно проверяются автоматическими тестами.");
        s.AppendLine();
        s.AppendLine("Между проходами выполняется явный GC для оценки удерживаемых объектов. Private bytes и рабочий набор включают резервирование среды и могут оставаться выше начального значения. Короткий прогон не доказывает отсутствие всех утечек.");
        s.AppendLine();
        s.AppendLine("В этом запуске не создаются окно WPF и устройство DirectX. Не измерены FPS GPU, задержки ввода, видеопамять и длительная работа интерфейса. Отдельный прогон с открытым окном остаётся в плане. В самом приложении доступны время CPU-подготовки текущей геометрии и private bytes процесса.");
        s.AppendLine();
        s.AppendLine("## Повторный запуск");
        s.AppendLine();
        s.AppendLine("Из корня проекта:");
        s.AppendLine();
        s.AppendLine("    dotnet run --project .\\SubTerra.Diagnostics\\SubTerra.Diagnostics.csproj -c Release -- --passes 12 --output .\\docs\\validation");
        s.AppendLine();
        s.AppendLine("Подробные значения, средние, максимумы и объёмы выделений каждого режима находятся в performance.json рядом с этим отчётом.");
        return s.ToString();
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static string MiB(long value) => F(Math.Abs(value) < 5243 ? 0 : value / 1048576d);
}
