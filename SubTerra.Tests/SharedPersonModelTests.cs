using System.IO;
using System.Text.Json;
using SubTerra.Services;
using SubTerra.ViewModels;
using Xunit;

namespace SubTerra.Tests;

public sealed class SharedPersonModelTests
{
    private static PersonTrainingRun Run() => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        new double[9], Enumerable.Repeat(.5, 21).ToArray(), 6, 2, .3f,
        new(1, 1, 0, 0), new(1, 0, 0, 1), "Проверочная версия");

    [Fact]
    public void FreshProfileLoadsSharedWeightsWithoutCreatingLocalDataOrActivatingThem()
    {
        WithDirectory(directory =>
        {
            var package = Path.Combine(directory, "package.json");
            var local = Path.Combine(directory, "new-profile", "person-models.json");
            var run = Run();
            PersonTrainingJournal.ExportPackage(package, run);
            var original = File.ReadAllBytes(package);
            var journal = new PersonTrainingJournal(local, package);
            journal.Reload();
            Assert.Equal(run.Id, Assert.Single(journal.Runs).Id);
            Assert.Equal(run.Weights, journal.Runs[0].Weights);
            Assert.Equal(run.Note, journal.Runs[0].Note);
            Assert.True(journal.IsBundled(run.Id));
            Assert.Null(journal.Active); Assert.False(File.Exists(local));
            journal.Activate(run.Id);
            Assert.Equal(run.Id, journal.Active?.Id);
            Assert.Equal(original, File.ReadAllBytes(package));
            // Once selected, a model remains available even when a newer application ships another package.
            var next = Run(); PersonTrainingJournal.ExportPackage(package, next);
            var reopened = new PersonTrainingJournal(local, package); reopened.Reload();
            Assert.Equal(run.Id, reopened.Active?.Id);
            Assert.Equal(2, reopened.Runs.Count);
            reopened.Activate(null); reopened.Reload();
            Assert.Null(reopened.Active);
            var localOnly = new PersonTrainingJournal(local); localOnly.Reload();
            Assert.Equal(run.Id, Assert.Single(localOnly.Runs).Id);
        });
    }

    [Fact]
    public void SharedPackageDoesNotReplaceExistingChoiceAndDuplicateIdsAreListedOnce()
    {
        WithDirectory(directory =>
        {
            var package = Path.Combine(directory, "package.json");
            var local = Path.Combine(directory, "local.json");
            var personal = Run(); var shared = Run();
            var original = new PersonTrainingJournal(local); original.Add(personal); original.Activate(personal.Id);
            PersonTrainingJournal.ExportPackage(package, shared);
            var combined = new PersonTrainingJournal(local, package); combined.Reload();
            Assert.Equal(personal.Id, combined.Active?.Id); Assert.Equal(2, combined.Runs.Count);
            combined.Activate(shared.Id); combined.Reload();
            Assert.Equal(2, combined.Runs.Count);
            Assert.Equal(shared.Id, combined.Active?.Id);
            var reopened = new PersonTrainingJournal(local); reopened.Reload();
            Assert.Equal(shared.Id, reopened.Active?.Id);
            Assert.Throws<InvalidOperationException>(() => combined.Add(shared));
        });
    }

    [Fact]
    public void ExportOnlyContainsTheSelectedModelAndNeverActivatesItForTheRecipient()
    {
        WithDirectory(directory =>
        {
            var file = Path.Combine(directory, "shared.json");
            var run = Run(); PersonTrainingJournal.ExportPackage(file, run);
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(new[] { "Version", "ActiveId", "Runs" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("ActiveId").ValueKind);
            var item = Assert.Single(json.RootElement.GetProperty("Runs").EnumerateArray());
            Assert.Equal(new[] { "Id", "TrainedAt", "Weights", "Loss", "TrainingCount", "ValidationCount", "Threshold", "Baseline", "Trained", "Note" },
                item.EnumerateObject().Select(p => p.Name));
            var reader = new PersonTrainingJournal(null, file); reader.Reload();
            Assert.Null(reader.Active);
            Assert.Equal(run.Weights, Assert.Single(reader.Runs).Weights);
            Assert.Equal(run.Baseline, reader.Runs[0].Baseline);
            Assert.Equal(run.Trained, reader.Runs[0].Trained);
            var before = File.ReadAllBytes(file);
            Assert.Throws<InvalidDataException>(() => PersonTrainingJournal.ExportPackage(file, run with { Weights = [double.NaN] }));
            Assert.Equal(before, File.ReadAllBytes(file));
        });
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"Version\":99,\"ActiveId\":null,\"Runs\":[]}")]
    [InlineData("{\"Version\":1,\"ActiveId\":null,\"Runs\":[null]}")]
    public void CorruptSharedPackageDoesNotDisableTheLocalModel(string content)
    {
        WithDirectory(directory =>
        {
            var file = Path.Combine(directory, "package.json"); File.WriteAllText(file, content);
            var journal = new PersonTrainingJournal(null, file); var personal = Run();
            journal.Add(personal); journal.Activate(personal.Id); journal.Reload();
            Assert.NotNull(journal.BundledLoadError);
            Assert.Equal(personal.Id, journal.Active?.Id);
            Assert.Single(journal.Runs);
            var repaired = Run(); PersonTrainingJournal.ExportPackage(file, repaired); journal.Reload();
            Assert.Null(journal.BundledLoadError); Assert.Equal(2, journal.Runs.Count);
            Assert.Equal(personal.Id, journal.Active?.Id);
        });
    }

    [Fact]
    public void HistoryLimitPreservesTheSelectedSharedModel()
    {
        WithDirectory(directory =>
        {
            var file = Path.Combine(directory, "package.json"); var shared = Run();
            PersonTrainingJournal.ExportPackage(file, shared);
            var journal = new PersonTrainingJournal(null, file); journal.Reload(); journal.Activate(shared.Id);
            for (var i = 0; i < 15; i++) journal.Add(Run());
            Assert.Equal(10, journal.Runs.Count);
            Assert.Equal(shared.Id, journal.Active?.Id);
            Assert.Single(journal.Runs, r => r.Id == shared.Id);
        });
    }

    [Fact]
    public Task ShippedModelIsAvailableInTheAppWithoutLocalTrainingExamples() => ScanControlTests.OnSta(() =>
    {
        WithDirectory(directory =>
        {
            Assert.True(File.Exists(PersonTrainingJournal.BundledModelsPath));
            var package = new PersonTrainingJournal(null, PersonTrainingJournal.BundledModelsPath); package.Reload();
            var shared = Assert.Single(package.Runs);
            Assert.Equal(Guid.Parse("77cc426e-dae8-4497-9f47-fc45310bc216"), shared.Id);
            Assert.Contains("Экспериментальная", shared.Note);
            var store = new WorkspaceStateStore(directory); var access = new TestAccess();
            using var vm = new MainViewModel(new WorkspaceFeatureTests.MemoryScanSource(), new SessionStorage(),
                access: access, interaction: new TestInteraction(), workspaceStore: store);
            vm.OpenTrainingPanelCommand.Execute(null);
            Assert.Equal(shared.Id, vm.SelectedTrainingRun?.Id);
            Assert.Contains("поставки", vm.TrainingRunOrigin);
            Assert.False(vm.TrainPersonModelCommand.CanExecute(null));
            Assert.True(vm.ActivatePersonModelCommand.CanExecute(null));
            Assert.Contains("базовая YOLOX", vm.ActivePersonModelText);
            var shell = ThemeRenderingTests.ShellPreview(vm);
            ThemeRenderingTests.Layout(shell, 1040, 820);
            ThemeRenderingTests.CheckReadableText(shell);
            WorkspacePresentationTests.SavePreview(shell, "shared-model.png");
            var exported = Path.Combine(directory, "export.json");
            var requests = 0; vm.PersonModelExportRequested += (_, _) => requests++;
            vm.ExportPersonModelCommand.Execute(null); Assert.Equal(1, requests);
            Assert.True(vm.ExportSelectedPersonModel(exported));
            var receiver = new PersonTrainingJournal(null, exported); receiver.Reload();
            Assert.Equal(shared.Weights, Assert.Single(receiver.Runs).Weights);
            Assert.False(File.Exists(store.PersonExamplesPath));
            Assert.False(File.Exists(store.PersonModelsPath));
            vm.ActivatePersonModelCommand.Execute(null);
            var saved = new PersonTrainingJournal(store.PersonModelsPath); saved.Reload();
            Assert.Equal(shared.Id, saved.Active?.Id);
            access.Logout();
            Assert.False(vm.ExportPersonModelCommand.CanExecute(null));
            Assert.False(vm.ExportSelectedPersonModel(Path.Combine(directory, "guest.json")));
            Assert.False(File.Exists(Path.Combine(directory, "guest.json")));
        });
        return Task.CompletedTask;
    });

    private static void WithDirectory(Action<string> body)
    {
        var directory = WorkspaceFeatureTests.TemporaryDirectory();
        try { body(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
