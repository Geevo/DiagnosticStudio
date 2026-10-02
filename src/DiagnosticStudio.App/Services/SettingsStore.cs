using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiagnosticStudio.App.Services;

/// <summary>The few preferences that outlive a session.</summary>
public interface ISettingsStore
{
    /// <summary>Interface scale in percent; 100 is normal.</summary>
    int ZoomPercent { get; set; }

    /// <summary>Scale of the document pane alone, in percent; 100 is normal.</summary>
    int ContentZoomPercent { get; set; }

    /// <summary>Whether the application follows Windows or is always light or dark.</summary>
    ThemePreference Theme { get; set; }
}

/// <summary>
/// Keeps settings in <c>%AppData%\DiagnosticStudio\settings.json</c>. A missing, unreadable or damaged file means
/// defaults, and a failure to save is ignored: a preference is never worth interrupting an investigation for.
/// </summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private sealed class Data
    {
        public int ZoomPercent { get; set; } = 100;

        public int ContentZoomPercent { get; set; } = 100;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ThemePreference Theme { get; set; } = ThemePreference.System;
    }

    private readonly string _path;
    private readonly Data _data;

    public FileSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiagnosticStudio", "settings.json"))
    {
    }

    public FileSettingsStore(string path)
    {
        _path = path;
        _data = Load(path);
    }

    public int ZoomPercent
    {
        get => _data.ZoomPercent;
        set
        {
            if (_data.ZoomPercent == value)
            {
                return;
            }

            _data.ZoomPercent = value;
            Save();
        }
    }

    public int ContentZoomPercent
    {
        get => _data.ContentZoomPercent;
        set
        {
            if (_data.ContentZoomPercent == value)
            {
                return;
            }

            _data.ContentZoomPercent = value;
            Save();
        }
    }

    public ThemePreference Theme
    {
        get => _data.Theme;
        set
        {
            if (_data.Theme == value)
            {
                return;
            }

            _data.Theme = value;
            Save();
        }
    }

    private static Data Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new Data();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Data();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The setting applies for this session; it just will not be remembered.
        }
    }
}
