using SubTerra.Rendering;

namespace SubTerra.ViewModels;

public sealed partial class MainViewModel
{
    private bool _alignRoom = true, _keepRoomObjects = true, _cleanRoomNoise = true, _limitReconstructionToRoom;
    private double _floorHoleSize = 1.6, _wallHoleSize = .25;
    private bool _completeRoomShell = true, _repairWindows = true, _clipOutsideShell = true;
    public AsyncRelayCommand ReconstructRoomCommand { get; private set; } = null!;
    public bool HasReconstruction => _indoorPreview?.ReconstructionOptions is not null;
    public bool CanExportReconstruction => CanExport && HasReconstruction;
    public bool ReconstructionSettingsPending => HasReconstruction && _indoorPreview!.ReconstructionOptions != ReconstructionOptions;
    private RoomReconstructionOptions ReconstructionOptions => new(AlignRoom, FloorHoleSize, WallHoleSize, KeepRoomObjects, CleanRoomNoise, LimitReconstructionToRoom, CompleteRoomShell, RepairWindows, ClipOutsideShell);
    public bool CompleteRoomShell { get => _completeRoomShell; set { if (Set(ref _completeRoomShell, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public bool RepairWindows { get => _repairWindows; set { if (Set(ref _repairWindows, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public bool ClipOutsideShell { get => _clipOutsideShell; set { if (Set(ref _clipOutsideShell, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public bool AlignRoom { get => _alignRoom; set { if (Set(ref _alignRoom, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public bool KeepRoomObjects { get => _keepRoomObjects; set { if (Set(ref _keepRoomObjects, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public bool CleanRoomNoise { get => _cleanRoomNoise; set { if (Set(ref _cleanRoomNoise, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public bool LimitReconstructionToRoom { get => _limitReconstructionToRoom; set { if (Set(ref _limitReconstructionToRoom, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public double FloorHoleSize { get => _floorHoleSize; set { if (double.IsFinite(value) && value is >= 0 and <= 3 && Set(ref _floorHoleSize, value)) Raise(nameof(ReconstructionSettingsPending)); } }
    public double WallHoleSize { get => _wallHoleSize; set { if (double.IsFinite(value) && value is >= 0 and <= .5 && Set(ref _wallHoleSize, value)) Raise(nameof(ReconstructionSettingsPending)); } }

    public async Task ExportReconstructionAsync(string path)
    {
        if (!CanExportReconstruction || _indoorPreview is not { } preview) return;
        var token = BeginOperation("Экспорт реконструкции · отдельная модель с достроенными участками");
        try
        {
            var model = await Task.Run(() => RoomReconstructionBuilder.ExportModel(preview, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || !IsAdministrator || !ReferenceEquals(preview, _indoorPreview)) return;
            await _storage.ExportImportedPlyAsync(path, model, token, OperationProgress(token));
            Status = "Реконструкция экспортирована в отдельный PLY · содержит гипотезы поверхностей"; AddEvent(Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { OperationCancelled(); }
        catch (Exception ex) { ReportError("Не удалось экспортировать реконструкцию", ex); }
        finally { EndOperation(); }
    }
}
