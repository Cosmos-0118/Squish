using ClopWindows.Core.Settings;

namespace ClopWindows.App.ViewModels;

public sealed class VideoEncoderPresetOptionViewModel
{
    public VideoEncoderPresetOptionViewModel(VideoEncoderPreset preset, string title, string description)
    {
        Preset = preset;
        Title = title;
        Description = description;
    }

    public VideoEncoderPreset Preset { get; }

    public string Title { get; }

    public string Description { get; }
}
