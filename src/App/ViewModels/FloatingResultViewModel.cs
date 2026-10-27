using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Squish.App.Infrastructure;
using Squish.Core.Optimizers;
using Squish.Core.Shared;

namespace Squish.App.ViewModels;

public sealed class FloatingResultViewModel : ObservableObject
{
    private readonly Action<FloatingResultViewModel> _dismiss;
    private readonly Action<string>? _cancelRequest;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".mpg", ".mpeg", ".3gp", ".ts"
    };

    // Segoe Fluent Icons glyphs for the HUD's file tile.
    private const string PhotoGlyph = "";
    private const string VideoGlyph = "";
    private const string InfoGlyph = "";
    private const string SuccessGlyph = "";
    private const string WarningGlyph = "";

    private string _displayName = string.Empty;
    private string? _subtitle;
    private string _statusText = "Queued";
    private double _progress;
    private bool _isRunning = true;
    private bool _isSuccess;
    private bool _isFailure;
    private TimeSpan? _duration;
    private string _kindGlyph = PhotoGlyph;
    private string? _savingsText;

    private readonly RelayCommand _cancelCommand;

    private long? _originalBytes;
    private FilePath? _outputPath;

    public FloatingResultViewModel(string requestId, Action<FloatingResultViewModel> dismiss, Action<string>? cancelRequest = null)
    {
        RequestId = requestId;
        _dismiss = dismiss;
        _cancelRequest = cancelRequest;
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

    /// <summary>Icon shown in the HUD tile: photo or video for files, a status glyph for notifications.</summary>
    public string KindGlyph
    {
        get => _kindGlyph;
        private set => SetProperty(ref _kindGlyph, value);
    }

    /// <summary>Relative saving such as "-54%", or null when nothing was saved.</summary>
    public string? SavingsText
    {
        get => _savingsText;
        private set
        {
            if (SetProperty(ref _savingsText, value))
            {
                OnPropertyChanged(nameof(HasSavings));
            }
        }
    }

    public bool HasSavings => !string.IsNullOrEmpty(_savingsText);

    public void ApplyRequest(OptimisationRequest request)
    {
        SourcePath = request.SourcePath;
        SourceTag = ExtractSourceTag(request.Metadata);
        StartedAt = DateTimeOffset.UtcNow;
        _originalBytes = TryGetLength(request.SourcePath);
        DisplayName = request.SourcePath.Name;
        Subtitle = BuildSubtitle(request);
        KindGlyph = VideoExtensions.Contains(Path.GetExtension(request.SourcePath.Value)) ? VideoGlyph : PhotoGlyph;
        SavingsText = null;
        StatusText = "Queued";
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
    }

    public void ApplyResult(OptimisationResult result, OptimisationRequest? request)
    {
        IsRunning = false;
        IsSuccess = result.Status == OptimisationStatus.Succeeded;
        IsFailure = !IsSuccess && result.Status != OptimisationStatus.Cancelled;
        Progress = 100d;
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

        string? savings = null;
        var summary = IsSuccess ? BuildSizeSummary(_outputPath, _originalBytes, out savings) : null;
        SavingsText = savings;

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
        KindGlyph = style switch
        {
            FloatingHudNotificationStyle.Success => SuccessGlyph,
            FloatingHudNotificationStyle.Warning => WarningGlyph,
            _ => InfoGlyph
        };
        SavingsText = null;
        Progress = 0d;
        Duration = null;
        IsRunning = false;
        IsSuccess = style == FloatingHudNotificationStyle.Success;
        IsFailure = style == FloatingHudNotificationStyle.Warning;
    }

    private static string? BuildSizeSummary(FilePath? outputPath, long? originalBytes, out string? savings)
    {
        savings = null;
        if (originalBytes is null || outputPath is null)
        {
            return null;
        }

        var optimisedBytes = TryGetLength(outputPath.Value);
        if (optimisedBytes is null)
        {
            return null;
        }

        if (originalBytes.Value > 0)
        {
            var saved = 1d - (double)optimisedBytes.Value / originalBytes.Value;
            if (saved >= 0.01)
            {
                savings = string.Format(CultureInfo.CurrentCulture, "−{0:0}%", saved * 100d);
            }
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

}

public enum FloatingHudNotificationStyle
{
    Info,
    Success,
    Warning
}
