using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using ClopWindows.App.Infrastructure;
using ClopWindows.Core.Optimizers;
using ClopWindows.Core.Shared;

namespace ClopWindows.App.ViewModels;

public sealed class FloatingResultViewModel : ObservableObject
{
    private readonly Action<FloatingResultViewModel> _dismiss;
    private readonly Action<string>? _cancelRequest;
    private static readonly SolidColorBrush SuccessBrush = CreateBrush(122, 145, 255);
    private static readonly SolidColorBrush FailureBrush = CreateBrush(240, 120, 120);
    private static readonly SolidColorBrush RunningBrush = CreateBrush(120, 180, 255);

    private string _displayName = string.Empty;
    private string? _subtitle;
    private string _statusText = "Queued";
    private double _progress;
    private bool _isRunning = true;
    private bool _isSuccess;
    private bool _isFailure;
    private TimeSpan? _duration;
    private System.Windows.Media.Brush _statusBrush;

    private readonly RelayCommand _cancelCommand;

    private long? _originalBytes;
    private FilePath? _outputPath;

    public FloatingResultViewModel(string requestId, Action<FloatingResultViewModel> dismiss, Action<string>? cancelRequest = null)
    {
        RequestId = requestId;
        _dismiss = dismiss;
        _cancelRequest = cancelRequest;
        _statusBrush = RunningBrush;
        _cancelCommand = new RelayCommand(_ => _cancelRequest?.Invoke(RequestId), _ => _cancelRequest is not null && IsRunning);
        DismissCommand = new RelayCommand(_ => _dismiss(this));
    }

    public string RequestId { get; }

    public FilePath? SourcePath { get; private set; }

    public string SourceTag { get; private set; } = string.Empty;

    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

    public ICommand DismissCommand { get; }

    public ICommand CancelCommand => _cancelCommand;

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double Progress
    {
        get => _progress;
        set
        {
            var normalized = double.IsNaN(value) ? 0d : value;
            var clamped = Math.Clamp(normalized, 0d, 100d);
            SetProperty(ref _progress, clamped);
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        private set => SetProperty(ref _isSuccess, value);
    }

    public bool IsFailure
    {
        get => _isFailure;
        private set => SetProperty(ref _isFailure, value);
    }

    public TimeSpan? Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    public System.Windows.Media.Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public void ApplyRequest(OptimisationRequest request)
    {
        SourcePath = request.SourcePath;
        SourceTag = ExtractSourceTag(request.Metadata);
        StartedAt = DateTimeOffset.UtcNow;
        _originalBytes = TryGetLength(request.SourcePath);
        DisplayName = request.SourcePath.Name;
        Subtitle = BuildSubtitle(request);
        StatusText = "Queued";
        StatusBrush = RunningBrush;
        Progress = 0d;
        IsRunning = true;
        IsSuccess = false;
        IsFailure = false;
    }

    public void UpdateProgress(OptimisationProgress progress)
    {
        Progress = Math.Clamp(progress.Percentage, 0d, 100d);
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            StatusText = progress.Message;
        }
        else
        {
            StatusText = Progress >= 100d ? "Finalising" : "Optimising";
        }

        IsRunning = true;
        StatusBrush = RunningBrush;
    }

    public void ApplyResult(OptimisationResult result, OptimisationRequest? request)
    {
        IsRunning = false;
        IsSuccess = result.Status == OptimisationStatus.Succeeded;
        IsFailure = !IsSuccess && result.Status != OptimisationStatus.Cancelled;
        Progress = 100d;
        StatusBrush = IsSuccess ? SuccessBrush : FailureBrush;
        Duration = result.Duration;
        _outputPath = result.OutputPath ?? request?.SourcePath;

        StatusText = result.Status switch
        {
            OptimisationStatus.Succeeded => result.Message ?? "Optimised",
            OptimisationStatus.Unsupported => "Unsupported",
            OptimisationStatus.Cancelled => "Cancelled",
            OptimisationStatus.Failed => result.Message ?? "Failed",
            _ => result.Message ?? "Finished"
        };

        var summary = IsSuccess ? BuildSizeSummary(_outputPath, _originalBytes) : null;

        if (!string.IsNullOrWhiteSpace(summary))
        {
            StatusText = summary;
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            StatusText = result.Message;
        }
        else
        {
            StatusText = result.Status switch
            {
                OptimisationStatus.Succeeded => "Optimised",
                OptimisationStatus.Unsupported => "Unsupported",
                OptimisationStatus.Cancelled => "Cancelled",
                OptimisationStatus.Failed => "Failed",
                _ => "Finished"
            };
        }
    }

    public void ApplyNotification(string title, string? subtitle, string message, FloatingHudNotificationStyle style)
    {
        SourcePath = null;
        SourceTag = string.Empty;
        StartedAt = DateTimeOffset.UtcNow;
        DisplayName = title;
        Subtitle = subtitle;
        StatusText = message;
        Progress = 0d;
        Duration = null;
        IsRunning = false;
        IsSuccess = style == FloatingHudNotificationStyle.Success;
        IsFailure = style == FloatingHudNotificationStyle.Warning;
        StatusBrush = style switch
        {
            FloatingHudNotificationStyle.Success => SuccessBrush,
            FloatingHudNotificationStyle.Warning => FailureBrush,
            _ => RunningBrush
        };
    }

    private static string? BuildSizeSummary(FilePath? outputPath, long? originalBytes)
    {
        if (originalBytes is null || outputPath is null)
        {
            return null;
        }

        var optimisedBytes = TryGetLength(outputPath.Value);
        if (optimisedBytes is null)
        {
            return null;
        }

        var original = originalBytes.Value.HumanSize();
        var final = optimisedBytes.Value.HumanSize();
        return string.Format(CultureInfo.CurrentCulture, "{0} → {1}", original, final);
    }

    private static string ExtractSourceTag(IReadOnlyDictionary<string, object?> metadata)
    {
        if (metadata.TryGetValue("source", out var value) && value is string text)
        {
            return text;
        }

        return string.Empty;
    }

    private static string? BuildSubtitle(OptimisationRequest request)
    {
        var parts = new List<string>();
        if (request.SourcePath.Parent is { } parent)
        {
            parts.Add(parent.Value);
        }

        if (request.Metadata.TryGetValue("source", out var source) && source is string sourceText && !string.IsNullOrWhiteSpace(sourceText))
        {
            parts.Add(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(sourceText));
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return string.Join(" • ", parts);
    }

    private static long? TryGetLength(FilePath path)
    {
        try
        {
            var info = new FileInfo(path.Value);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    public bool TryGetResolvedFilePath(out string? fullPath, bool requireExistence = true)
    {
        var resolved = _outputPath ?? SourcePath;
        if (resolved is not FilePath path)
        {
            fullPath = null;
            return false;
        }

        fullPath = path.Value;
        if (requireExistence && !File.Exists(fullPath))
        {
            fullPath = null;
            return false;
        }

        return true;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public enum FloatingHudNotificationStyle
{
    Info,
    Success,
    Warning
}
