// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/03_FFMPEG_EXPORT_PIPELINE.md
// Invariants, constants, and threading models must match spec bit-for-bit.
using Avalonia.Controls;
using Avalonia.Threading;
using FortniteVideoSoftware.App.Services;
using FortniteVideoSoftware.App.ViewModels;
using FortniteVideoSoftware.Core.Media;
using FortniteVideoSoftware.Core.Infrastructure;

namespace FortniteVideoSoftware.App;

public partial class MainWindow
{
    private OutputSizeEstimator? _outputSizeEstimator;
    private LatestEstimateWorker<MainSizeRequest>? _mainSizeWorker;
    private MainSizeRequest? _lastSizeRequest;
    private OutputSizeEstimator SizeEstimator => _outputSizeEstimator ??= new(() =>
        BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries"));

    private void InitializeSizeEstimate()
    {
        _mainSizeWorker = new(SizeEstimator.EstimateMainAsync, estimate =>
        {
            _viewModel.Export.EstimatedFileSizeText = estimate.Text;
            _viewModel.Export.EstimatedFileSizeDescription = estimate.Megabytes.HasValue
                ? "Estimated size of the finished video, including sound. The actual file size may differ."
                : "The size will appear when the video details are available.";
        }, action => Dispatcher.UIThread.Post(action), OutputSizeEstimator.QuickMainEstimate);
        _viewModel.SizeEstimateRequested += UpdateEstimatedQuality;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.LoadedVideoPath) or nameof(MainViewModel.IsAddMeme)
                or nameof(MainViewModel.SelectedMemeItem) or nameof(MainViewModel.MusicWizardResult)
                or nameof(MainViewModel.VoiceOverResult)) UpdateEstimatedQuality();
        };
    }

    private MainSizeRequest CaptureSizeRequest()
    {
        double start = _trimStartSet ? _trimStartMs : 0;
        double end = _trimEndSet && _trimEndMs > start ? _trimEndMs : _loadedVideoDurationMs;
        var meme = this.FindControl<ComboBox>("MemeComboBox")?.SelectedItem as MemeItem;
        string? legacy = IsAddMeme && meme is { IsDownloadAction: false } ? meme.FullPath : null;
        return new(_loadedVideoPath, _loadedVideoDurationMs, start, end, _baseSpeed,
            BuildExportSpeedSegments().ToArray(), _cuts.ToArray(), _memePlacements.ToArray(), legacy,
            IsPortraitMode, _viewModel.Export.QualitySliderValue);
    }

    private void RequestSizeEstimate()
    {
        if (_mainSizeWorker == null) return;
        var request = CaptureSizeRequest();
        // SIZEESTIMATE_01 — recovery also saves on scrubbing/volume. Ignore unchanged export inputs.
        if (_lastSizeRequest is { } previous &&
            (previous with { Segments = request.Segments, Cuts = request.Cuts, Memes = request.Memes }) == request &&
            previous.Segments.SequenceEqual(request.Segments) && previous.Cuts.SequenceEqual(request.Cuts) &&
            previous.Memes.SequenceEqual(request.Memes)) return;
        bool newVideo = _lastSizeRequest?.Path != request.Path;
        _lastSizeRequest = request;
        if (string.IsNullOrWhiteSpace(request.Path))
            _viewModel.Export.EstimatedFileSizeText = "—";
        else if (newVideo) _viewModel.Export.EstimatedFileSizeText = "Calculating…";
        _viewModel.Export.EstimatedFileSizeDescription = "Updating the estimated size…";
        _mainSizeWorker.Request(request);
    }
}
