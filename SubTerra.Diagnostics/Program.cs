using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SubTerra.Models;
using SubTerra.Rendering;
using SubTerra.Services;
using SubTerra.Simulation;

namespace SubTerra.Diagnostics;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length == 2 && args[0] == "--stress-points")
                return RunImportedStress(int.Parse(args[1], CultureInfo.InvariantCulture));
            var passes = 12;
            var output = Path.GetFullPath(Path.Combine("artifacts", "diagnostics"));
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--passes" && i + 1 < args.Length) passes = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--output" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
                else throw new ArgumentException("Параметры: --passes 12 --output <папка отчёта> или --stress-points <число точек> (только консоль).");
            }
            if (passes is < 3 or > 100) throw new ArgumentOutOfRangeException(nameof(passes), "Число проходов: 3–100.");
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var clock = Stopwatch.StartNew();
            Console.WriteLine("Прогрев: 2 прохода; измеряются генерация и CPU-подготовка геометрии, без GPU.");
            RunPass(0, ScenarioDefinition.All[0], 360, cancellation.Token);
            RunPass(0, ScenarioDefinition.All[0], 1280, cancellation.Token);
            var initialMemory = CaptureMemory(collect: true);
            var results = new List<PassResult>();
            for (var pass = 1; pass <= passes; pass++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var scene = ScenarioDefinition.All[(pass - 1) % ScenarioDefinition.All.Count];
                var result = RunPass(pass, scene, pass % 2 == 0 ? 1280 : 720, cancellation.Token);
                result = result with { MemoryAfterCollection = CaptureMemory(collect: true) };
                results.Add(result);
                Console.WriteLine(FormattableString.Invariant(
                    $"{pass:00}/{passes}: {scene.Id}, {result.PointCount:N0} точек, генерация {result.GenerationMs:F1} мс, облако p95 {result.LimitedCloud.P95Ms:F2} мс, managed после GC {result.MemoryAfterCollection.ManagedBytes / 1048576d:F1} МиБ"));
            }
            var report = new BenchmarkReport(DateTimeOffset.UtcNow, clock.Elapsed.TotalSeconds,
                RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                ReadCpuName(), Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                typeof(SceneGeometry).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
                2, 60_000, initialMemory, results);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(output, "performance.md"), ReportMarkdown.Build(report), new UTF8Encoding(false));
            Console.WriteLine($"Отчёт: {Path.Combine(output, "performance.md")}");
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Замер отменён."); return 2; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static int RunImportedStress(int count)
    {
        if (count is < 1000 or > ImportedScan.MaximumPoints) throw new ArgumentOutOfRangeException(nameof(count), "Число точек: 1000–5000000.");
        var stem = Path.Combine(Path.GetTempPath(), $"subterra-stress-{Guid.NewGuid():N}");
        var scanPath = stem + ".ply"; var sessionPath = stem + ".subterra";
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using (var stream = File.Create(scanPath))
            {
                stream.Write(Encoding.ASCII.GetBytes($"ply\nformat binary_little_endian 1.0\nelement vertex {count}\nproperty float x\nproperty float y\nproperty float z\nend_header\n"));
                using var writer = new BinaryWriter(stream);
                for (var i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) cancellation.Token.ThrowIfCancellationRequested();
                    var u = (i / 6 % 1000) / 999f; var v = (i / 6000 % 1000) / 999f;
                    var p = (i % 6) switch
                    {
                        0 => new System.Numerics.Vector3(u * 6, v * 5, 0), 1 => new(u * 6, v * 5, 3),
                        2 => new(0, u * 5, v * 3), 3 => new(6, u * 5, v * 3), 4 => new(u * 6, 0, v * 3), _ => new(u * 6, 5, v * 3)
                    };
                    writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z);
                }
            }
            var clock = Stopwatch.StartNew(); var allocated = GC.GetTotalAllocatedBytes(true);
            var scan = new ScanImporter().ImportAsync(scanPath, new(ScanUnit.Meters, LocalOrigin:false), cancellation.Token).GetAwaiter().GetResult();
            var importedMs = clock.Elapsed.TotalMilliseconds;
            var project = ScanProject.FromSession(scan.ToSession()); clock.Restart();
            var view = ScanProjectView.PrepareAsync(project, cancellation.Token).GetAwaiter().GetResult();
            var cloud = view.Geometry!.Cloud(60_000, null); var preparedMs = clock.Elapsed.TotalMilliseconds;
            if (cloud.Positions!.Count > 60_000) throw new InvalidOperationException("Нарушен лимит отображения.");
            clock.Restart(); var options = new StructureAnalysisOptions();
            var analysis = new PlaneStructureAnalyzer().AnalyzeAsync(project, options, null, cancellation.Token).GetAwaiter().GetResult();
            var analysisMs = clock.Elapsed.TotalMilliseconds;
            project = project with { Structure = StructureProvenance.Create(project, analysis, cancellation.Token) }; clock.Restart();
            var storage = new SessionStorage(); storage.SaveAsync(sessionPath, project.ToSession(), 1, cancellation.Token).GetAwaiter().GetResult();
            var savedMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
            var restored = storage.LoadAsync(sessionPath, cancellation.Token).GetAwaiter().GetResult();
            var loadedMs = clock.Elapsed.TotalMilliseconds;
            if (restored.Project!.Active!.Source.Points.Length != count || restored.Project.Active.Source.Points[^1] != scan.Points[^1])
                throw new InvalidOperationException("После повторного открытия изменились исходные измерения.");
            using var process = Process.GetCurrentProcess(); process.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Synthetic = true, Points = count, ImportMs = importedMs, ViewMs = preparedMs, AnalysisMs = analysisMs,
                SaveMs = savedMs, LoadMs = loadedMs, RenderedPoints = cloud.Positions.Count, AnalysisSamples = analysis.SampleCount,
                Planes = analysis.Surfaces.Length, AllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576d,
                PrivateMiB = process.PrivateMemorySize64 / 1048576d, PeakWorkingSetMiB = process.PeakWorkingSet64 / 1048576d,
                Scope = "CPU and file I/O; no GPU or hardware accuracy measurement"
            }, new JsonSerializerOptions { WriteIndented = true }));
            GC.KeepAlive(view); GC.KeepAlive(restored); return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (File.Exists(scanPath)) File.Delete(scanPath);
            if (File.Exists(sessionPath)) File.Delete(sessionPath);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PassResult RunPass(int pass, ScenarioDefinition scenario, int rays, CancellationToken cancellationToken)
    {
        var settings = new ScanSettings(20, rays, 180, StartProgress: pass % 4 == 0 && pass != 0 ? .35f : 0);
        var clock = Stopwatch.StartNew();
        var data = new SimulatedScanSource().GenerateAsync(scenario, settings, null, cancellationToken).GetAwaiter().GetResult();
        var generation = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var geometry = new SceneGeometry(data.Scene!, data.Session);
        var preparation = clock.Elapsed.TotalMilliseconds;
        var count = 0;
        var prefixes = data.Session.Frames.Select(f => count += f.Points.Length).ToArray();
        var full = Measure(prefixes.Length, i => geometry.Cloud(prefixes[i], false, int.MaxValue), cancellationToken);
        var limited = Measure(prefixes.Length, i => geometry.Cloud(prefixes[i], false, 60_000), cancellationToken);
        var surface = Measure(prefixes.Length, i => geometry.SurfaceLayers(
            [new(settings.StartProgress, data.Session.Frames[i].Progress)], true), cancellationToken);
        var growing = new SceneGeometry(data.Scene!);
        var streaming = Measure(prefixes.Length, i =>
        {
            growing.AppendFrame(data.Session.Frames[i]);
            return growing.Cloud(prefixes[i], false, 60_000);
        }, cancellationToken);
        var displayed = geometry.Cloud(count, false, 60_000).Positions?.Count
            ?? throw new InvalidOperationException("Не создано облако для просмотра.");
        if (displayed > 60_000) throw new InvalidOperationException("Нарушен лимит просмотра.");
        using var process = Process.GetCurrentProcess();
        return new(pass, scenario.Id, rays, settings.StartProgress, count, displayed, generation, preparation,
            full, limited, surface, streaming, process.PeakWorkingSet64, new(0, 0, 0));
    }

    private static TimingSummary Measure(int count, Func<int, object> prepare, CancellationToken cancellationToken)
    {
        var timings = new double[count];
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Stopwatch.GetTimestamp();
            var result = prepare(i);
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            GC.KeepAlive(result);
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Array.Sort(timings);
        return new(count, timings.Average(), timings[(int)Math.Ceiling(count * .95) - 1], timings[^1], allocated);
    }

    private static MemorySnapshot CaptureMemory(bool collect)
    {
        if (collect)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        using var process = Process.GetCurrentProcess();
        return new(GC.GetTotalMemory(false), process.PrivateMemorySize64, process.WorkingSet64);
    }

    private static string ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Не определён";
    }
}

internal sealed record MemorySnapshot(long ManagedBytes, long PrivateBytes, long WorkingSetBytes);
internal sealed record TimingSummary(int Frames, double MeanMs, double P95Ms, double MaximumMs, long AllocatedBytes);
internal sealed record PassResult(int Pass, string Scenario, int RaysPerFrame, float StartProgress, int PointCount,
    int DisplayedPoints, double GenerationMs, double ScenePreparationMs, TimingSummary FullCloud,
    TimingSummary LimitedCloud, TimingSummary Surface, TimingSummary StreamingCloud,
    long ProcessPeakWorkingSetBytes, MemorySnapshot MemoryAfterCollection);
internal sealed record BenchmarkReport(DateTimeOffset FinishedUtc, double ElapsedSeconds, string OperatingSystem,
    string Runtime, string Architecture, string Cpu, int LogicalProcessors, long GcAvailableMemoryBytes,
    string Configuration, int WarmupPasses, int PointLimit, MemorySnapshot InitialMemory, List<PassResult> Passes);
