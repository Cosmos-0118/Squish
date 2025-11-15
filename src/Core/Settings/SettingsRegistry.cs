using System.Collections.Immutable;
using ClopWindows.Core.Shared;

namespace ClopWindows.Core.Settings;

public static class SettingsRegistry
{
    private const string DefaultSameFolderTemplate = "%f-optimised";
    private const string DefaultSpecificFolderTemplate = "%P/optimised/%f";

    private static readonly string DesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static readonly SettingKey<bool> FinishedOnboarding = Bool("finishedOnboarding", false);
    public static readonly SettingKey<bool> ShowMenubarIcon = Bool("showMenubarIcon", true);
    public static readonly SettingKey<bool> EnableFloatingResults = Bool("enableFloatingResults", true);
    public static readonly SettingKey<bool> OptimiseTiff = Bool("optimiseTIFF", true);
    public static readonly SettingKey<bool> EnableClipboardOptimiser = Bool("enableClipboardOptimiser", true);
    public static readonly SettingKey<bool> OptimiseVideoClipboard = Bool("optimiseVideoClipboard", true);
    public static readonly SettingKey<bool> OptimiseClipboardFileDrops = Bool("optimiseClipboardFileDrops", false);
    public static readonly SettingKey<bool> OptimiseImagePathClipboard = Bool("optimiseImagePathClipboard", false);
    public static readonly SettingKey<bool> OptimisePdfClipboard = Bool("optimisePdfClipboard", true);
    public static readonly SettingKey<bool> StripMetadata = Bool("stripMetadata", true);
    public static readonly SettingKey<bool> PreserveDates = Bool("preserveDates", true);
    public static readonly SettingKey<bool> PreserveColorMetadata = Bool("preserveColorMetadata", true);
    public static readonly SettingKey<bool> ReplaceOptimisedFilesInPlace = Bool("replaceOptimisedFilesInPlace", false);
    public static readonly SettingKey<bool> DeleteOriginalAfterConversion = Bool("deleteOriginalAfterConversion", false);

    public static readonly SettingKey<string> Workdir = new("workdir", ClopPaths.WorkRoot.Value);
    public static readonly SettingKey<CleanupInterval> WorkdirCleanupInterval = new("workdirCleanupInterval", CleanupInterval.Every3Days);

    public static readonly SettingKey<HashSet<string>> FormatsToConvertToJpeg = Set("formatsToConvertToJPEG", new[] { "webp", "avif", "heic", "bmp" });
    public static readonly SettingKey<HashSet<string>> FormatsToConvertToPng = Set("formatsToConvertToPNG", new[] { "tiff" });
    public static readonly SettingKey<HashSet<string>> FormatsToConvertToMp4 = Set("formatsToConvertToMP4", new[] { "mov", "mpg", "mpeg", "webm" });

    public static readonly SettingKey<ConvertedFileBehaviour> ConvertedImageBehaviour = new("convertedImageBehaviour", ConvertedFileBehaviour.SameFolder);
    public static readonly SettingKey<ConvertedFileBehaviour> ConvertedVideoBehaviour = new("convertedVideoBehaviour", ConvertedFileBehaviour.SameFolder);

    public static readonly SettingKey<OptimisedFileBehaviour> OptimisedImageBehaviour = new("optimisedImageBehaviour", OptimisedFileBehaviour.InPlace);
    public static readonly SettingKey<OptimisedFileBehaviour> OptimisedVideoBehaviour = new("optimisedVideoBehaviour", OptimisedFileBehaviour.InPlace);
    public static readonly SettingKey<OptimisedFileBehaviour> OptimisedPdfBehaviour = new("optimisedPDFBehaviour", OptimisedFileBehaviour.InPlace);

    public static readonly SettingKey<string> SameFolderNameTemplateImage = new("sameFolderNameTemplateImage", DefaultSameFolderTemplate);
    public static readonly SettingKey<string> SameFolderNameTemplateVideo = new("sameFolderNameTemplateVideo", DefaultSameFolderTemplate);
    public static readonly SettingKey<string> SameFolderNameTemplatePdf = new("sameFolderNameTemplatePDF", DefaultSameFolderTemplate);

    public static readonly SettingKey<string> SpecificFolderNameTemplateImage = new("specificFolderNameTemplateImage", DefaultSpecificFolderTemplate);
    public static readonly SettingKey<string> SpecificFolderNameTemplateVideo = new("specificFolderNameTemplateVideo", DefaultSpecificFolderTemplate);
    public static readonly SettingKey<string> SpecificFolderNameTemplatePdf = new("specificFolderNameTemplatePDF", DefaultSpecificFolderTemplate);

    public static readonly SettingKey<bool> CapVideoFps = Bool("capVideoFPS", true);
    public static readonly SettingKey<float> TargetVideoFps = Float("targetVideoFPS", 60);
    public static readonly SettingKey<float> MinVideoFps = Float("minVideoFPS", 30);
    public static readonly SettingKey<bool> RemoveAudioFromVideos = Bool("removeAudioFromVideos", false);
    public static readonly SettingKey<bool> UseAggressiveOptimisationMp4 = Bool("useAggressiveOptimisationMP4", false);
    public static readonly SettingKey<bool> UseAggressiveOptimisationJpeg = Bool("useAggressiveOptimisationJPEG", false);
    public static readonly SettingKey<bool> UseAggressiveOptimisationPng = Bool("useAggressiveOptimisationPNG", false);
    public static readonly SettingKey<bool> UseAggressiveOptimisationGif = Bool("useAggressiveOptimisationGIF", false);
    public static readonly SettingKey<bool> UseAggressiveOptimisationPdf = Bool("useAggressiveOptimisationPDF", true);

    public static readonly SettingKey<string[]> ImageDirs = Array("imageDirs", DesktopPath);
    public static readonly SettingKey<string[]> VideoDirs = Array("videoDirs", DesktopPath);
    public static readonly SettingKey<string[]> PdfDirs = Array("pdfDirs");
    public static readonly SettingKey<bool> EnableAutomaticImageOptimisations = Bool("enableAutomaticImageOptimisations", true);
    public static readonly SettingKey<bool> EnableAutomaticVideoOptimisations = Bool("enableAutomaticVideoOptimisations", true);
    public static readonly SettingKey<bool> EnableAutomaticPdfOptimisations = Bool("enableAutomaticPDFOptimisations", true);

    public static readonly SettingKey<int> MaxVideoSizeMb = Int("maxVideoSizeMB", 500);
    public static readonly SettingKey<int> MaxImageSizeMb = Int("maxImageSizeMB", 50);
    public static readonly SettingKey<int> MaxPdfSizeMb = Int("maxPDFSizeMB", 100);
    public static readonly SettingKey<int> MaxVideoFileCount = Int("maxVideoFileCount", 1);
    public static readonly SettingKey<int> MaxImageFileCount = Int("maxImageFileCount", 4);
    public static readonly SettingKey<int> MaxPdfFileCount = Int("maxPDFFileCount", 2);

    public static readonly SettingKey<HashSet<string>> ImageFormatsToSkip = Set("imageFormatsToSkip", new[] { "tiff" });
    public static readonly SettingKey<HashSet<string>> VideoFormatsToSkip = Set("videoFormatsToSkip", new[] { "mkv", "m4v" });

    public static readonly SettingKey<bool> AdaptiveVideoSize = Bool("adaptiveVideoSize", true);
    public static readonly SettingKey<bool> AdaptiveImageSize = Bool("adaptiveImageSize", false);
    public static readonly SettingKey<bool> DownscaleRetinaImages = Bool("downscaleRetinaImages", false);
    public static readonly SettingKey<bool> CopyImageFilePath = Bool("copyImageFilePath", true);
    public static readonly SettingKey<bool> UseCustomNameTemplateForClipboardImages = Bool("useCustomNameTemplateForClipboardImages", false);
    public static readonly SettingKey<string> CustomNameTemplateForClipboardImages = new("customNameTemplateForClipboardImages", string.Empty);
    public static readonly SettingKey<int> LastAutoIncrementingNumber = Int("lastAutoIncrementingNumber", 0);

    public static readonly SettingKey<float> FloatingHudScale = Float("floatingHudScale", 1f);
    public static readonly SettingKey<float> FloatingHudWidthScale = Float("floatingHudWidthScale", 1f);
    public static readonly SettingKey<float> FloatingHudHeightScale = Float("floatingHudHeightScale", 1f);

    public static readonly SettingKey<bool> EnableDragAndDrop = Bool("enableDragAndDrop", true);
    public static readonly SettingKey<bool> OnlyShowDropZoneOnOption = Bool("onlyShowDropZoneOnOption", false);
    public static readonly SettingKey<bool> OnlyShowPresetZonesOnControlTapped = Bool("onlyShowPresetZonesOnControlTapped", false);
    public static readonly SettingKey<bool> ShowImages = Bool("showImages", true);
    public static readonly SettingKey<bool> ShowCompactImages = Bool("showCompactImages", false);
    public static readonly SettingKey<bool> AutoHideFloatingResults = Bool("autoHideFloatingResults", true);
    public static readonly SettingKey<int> AutoHideFloatingResultsAfter = Int("autoHideFloatingResultsAfter", 30);
    public static readonly SettingKey<int> AutoHideClipboardResultAfter = Int("autoHideClipboardResultAfter", 3);
    public static readonly SettingKey<int> AutoClearCompactResultsAfter = Int("autoClearAllCompactResultsAfter", 120);
    public static readonly SettingKey<bool> FloatingHudPinned = Bool("floatingHudPinned", false);
    public static readonly SettingKey<double> FloatingHudPinnedLeft = new("floatingHudPinnedLeft", double.NaN);
    public static readonly SettingKey<double> FloatingHudPinnedTop = new("floatingHudPinnedTop", double.NaN);

    public static readonly SettingKey<bool> AutoCopyToClipboard = Bool("autoCopyToClipboard", true);
    public static readonly SettingKey<bool> CliInstalled = Bool("cliInstalled", true);
    public static readonly SettingKey<bool> EnableCrossAppAutomation = Bool("enableCrossAppAutomation", true);
    public static readonly SettingKey<int> AutomationHttpPort = Int("automationHttpPort", 58732);
    public static readonly SettingKey<string> AutomationAccessToken = new("automationAccessToken", string.Empty);
    public static readonly SettingKey<bool> EnableTeamsAdaptiveCards = Bool("enableTeamsAdaptiveCards", true);

    public static readonly SettingKey<string> ShortcutBrowseFiles = new("shortcutBrowseFiles", "Ctrl+O");
    public static readonly SettingKey<string> ShortcutShowSettings = new("shortcutShowSettings", "Ctrl+OemComma");
    public static readonly SettingKey<string> ShortcutShowOnboarding = new("shortcutShowOnboarding", "Ctrl+1");
    public static readonly SettingKey<string> ShortcutShowCompare = new("shortcutShowCompare", "Ctrl+2");
    public static readonly SettingKey<string> ShortcutShowSettingsNavigation = new("shortcutShowSettingsNavigation", "Ctrl+3");
    public static readonly SettingKey<string> ShortcutShowMainWindow = new("shortcutShowMainWindow", "Ctrl+Shift+Space");
    public static readonly SettingKey<string> ShortcutToggleFloatingResults = new("shortcutToggleFloatingResults", "Ctrl+Shift+F");
    public static readonly SettingKey<string> ShortcutToggleClipboardOptimiser = new("shortcutToggleClipboardOptimiser", "Ctrl+Shift+C");

    public static readonly SettingKey<List<CropSize>> SavedCropSizes = new("savedCropSizes", DefaultCropSizes);
    public static readonly SettingKey<bool> PauseAutomaticOptimisations = Bool("pauseAutomaticOptimisations", false);
    public static readonly SettingKey<bool> SyncSettingsCloud = Bool("syncSettingsCloud", true);
    public static readonly SettingKey<bool> AllowClopToAppearInScreenshots = Bool("allowClopToAppearInScreenshots", false);
    public static readonly SettingKey<AppThemeMode> AppThemeMode = new("appThemeMode", global::ClopWindows.Core.Settings.AppThemeMode.FollowSystem);

    public static readonly ImmutableArray<ISettingKey> AllKeys = ImmutableArray.Create<ISettingKey>(
        FinishedOnboarding,
        ShowMenubarIcon,
        EnableFloatingResults,
        OptimiseTiff,
        EnableClipboardOptimiser,
        OptimiseVideoClipboard,
        OptimiseClipboardFileDrops,
        OptimisePdfClipboard,
        OptimiseImagePathClipboard,
        StripMetadata,
        PreserveDates,
        PreserveColorMetadata,
        ReplaceOptimisedFilesInPlace,
        DeleteOriginalAfterConversion,
        Workdir,
        WorkdirCleanupInterval,
        FormatsToConvertToJpeg,
        FormatsToConvertToPng,
        FormatsToConvertToMp4,
        ConvertedImageBehaviour,
        ConvertedVideoBehaviour,
        OptimisedImageBehaviour,
        OptimisedVideoBehaviour,
        OptimisedPdfBehaviour,
        SameFolderNameTemplateImage,
        SameFolderNameTemplateVideo,
        SameFolderNameTemplatePdf,
        SpecificFolderNameTemplateImage,
        SpecificFolderNameTemplateVideo,
        SpecificFolderNameTemplatePdf,
        CapVideoFps,
        TargetVideoFps,
        MinVideoFps,
        RemoveAudioFromVideos,
        UseAggressiveOptimisationMp4,
        UseAggressiveOptimisationJpeg,
        UseAggressiveOptimisationPng,
        UseAggressiveOptimisationGif,
        UseAggressiveOptimisationPdf,
        ImageDirs,
        VideoDirs,
        PdfDirs,
        EnableAutomaticImageOptimisations,
        EnableAutomaticVideoOptimisations,
        EnableAutomaticPdfOptimisations,
        MaxVideoSizeMb,
        MaxImageSizeMb,
        MaxPdfSizeMb,
        MaxVideoFileCount,
        MaxImageFileCount,
        MaxPdfFileCount,
        ImageFormatsToSkip,
        VideoFormatsToSkip,
        AdaptiveVideoSize,
        AdaptiveImageSize,
        DownscaleRetinaImages,
        CopyImageFilePath,
        UseCustomNameTemplateForClipboardImages,
        CustomNameTemplateForClipboardImages,
        LastAutoIncrementingNumber,
        EnableDragAndDrop,
        OnlyShowDropZoneOnOption,
        OnlyShowPresetZonesOnControlTapped,
        ShowImages,
        ShowCompactImages,
        AutoHideFloatingResults,
        AutoHideFloatingResultsAfter,
        AutoHideClipboardResultAfter,
        AutoClearCompactResultsAfter,
        FloatingHudPinned,
        FloatingHudPinnedLeft,
        FloatingHudPinnedTop,
        FloatingHudScale,
        FloatingHudWidthScale,
        FloatingHudHeightScale,
        AutoCopyToClipboard,
        CliInstalled,
        EnableCrossAppAutomation,
        AutomationHttpPort,
        AutomationAccessToken,
        EnableTeamsAdaptiveCards,
        ShortcutBrowseFiles,
        ShortcutShowSettings,
        ShortcutShowOnboarding,
        ShortcutShowCompare,
        ShortcutShowSettingsNavigation,
        ShortcutShowMainWindow,
        ShortcutToggleFloatingResults,
        ShortcutToggleClipboardOptimiser,
        SavedCropSizes,
        PauseAutomaticOptimisations,
        SyncSettingsCloud,
        AllowClopToAppearInScreenshots,
        AppThemeMode
    );

    private static SettingKey<bool> Bool(string name, bool defaultValue) => new(name, defaultValue);
    private static SettingKey<int> Int(string name, int defaultValue) => new(name, defaultValue);
    private static SettingKey<float> Float(string name, float defaultValue) => new(name, defaultValue);
    private static SettingKey<string[]> Array(string name, params string[] defaults) => new(name, defaults);
    private static SettingKey<HashSet<string>> Set(string name, IEnumerable<string> defaults) => new(name, new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase));

    private static List<CropSize> DefaultCropSizes =>
        new()
        {
            new CropSize(1920, 1080, "1080p"),
            new CropSize(1280, 720, "720p"),
            new CropSize(1440, 900, "Mac App Store"),
            new CropSize(1200, 630, "OpenGraph"),
            new CropSize(1600, 900, "Twitter"),
            new CropSize(128, 128, "Small Square"),
            new CropSize(512, 512, "Medium Square"),
            new CropSize(1024, 1024, "Large Square")
        };
}
