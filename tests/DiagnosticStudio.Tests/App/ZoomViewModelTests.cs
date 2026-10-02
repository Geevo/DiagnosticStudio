using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.Tests.App;

public sealed class ZoomViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-zoom-" + Guid.NewGuid().ToString("N"));

    public ZoomViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class MemorySettings : ISettingsStore
    {
        public int ZoomPercent { get; set; } = 100;
        public int Saves { get; private set; }

        int ISettingsStore.ZoomPercent
        {
            get => ZoomPercent;
            set
            {
                ZoomPercent = value;
                Saves++;
            }
        }
    }

    [Fact]
    public void Zoom_starts_at_the_saved_level()
    {
        var zoom = new ZoomViewModel(new MemorySettings { ZoomPercent = 130 });

        Assert.Equal(130, zoom.Percent);
        Assert.Equal(1.3, zoom.Scale, 3);
        Assert.Equal("130%", zoom.Text);
        Assert.True(zoom.IsScaled);
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(5000, 300)]
    [InlineData(-5, 50)]
    public void A_saved_level_outside_the_range_is_brought_into_it(int saved, int expected)
    {
        Assert.Equal(expected, new ZoomViewModel(new MemorySettings { ZoomPercent = saved }).Percent);
    }

    [Fact]
    public void Zooming_in_and_out_moves_in_steps_of_ten()
    {
        var zoom = new ZoomViewModel(new MemorySettings());

        zoom.ZoomInCommand.Execute(null);
        zoom.ZoomInCommand.Execute(null);
        Assert.Equal(120, zoom.Percent);

        zoom.ZoomOutCommand.Execute(null);
        Assert.Equal(110, zoom.Percent);
    }

    [Fact]
    public void Zoom_stops_at_the_limits_and_the_commands_say_so()
    {
        var zoom = new ZoomViewModel(new MemorySettings { ZoomPercent = 290 });

        zoom.ZoomInCommand.Execute(null);
        Assert.Equal(300, zoom.Percent);
        Assert.False(zoom.ZoomInCommand.CanExecute(null));
        Assert.True(zoom.ZoomOutCommand.CanExecute(null));
        zoom.ZoomInCommand.Execute(null); // does nothing
        Assert.Equal(300, zoom.Percent);

        var small = new ZoomViewModel(new MemorySettings { ZoomPercent = 60 });
        small.ZoomOutCommand.Execute(null);
        Assert.Equal(50, small.Percent);
        Assert.False(small.ZoomOutCommand.CanExecute(null));
    }

    [Fact]
    public void Reset_returns_to_one_hundred_percent_and_is_only_offered_when_scaled()
    {
        var zoom = new ZoomViewModel(new MemorySettings());
        Assert.False(zoom.ResetCommand.CanExecute(null));
        Assert.False(zoom.IsScaled);

        zoom.ZoomInCommand.Execute(null);
        Assert.True(zoom.ResetCommand.CanExecute(null));

        zoom.ResetCommand.Execute(null);
        Assert.Equal(100, zoom.Percent);
        Assert.False(zoom.IsScaled);
    }

    [Fact]
    public void The_mouse_wheel_zooms_in_on_up_and_out_on_down()
    {
        var zoom = new ZoomViewModel(new MemorySettings());

        zoom.Wheel(120);
        zoom.Wheel(120);
        Assert.Equal(120, zoom.Percent);

        zoom.Wheel(-120);
        Assert.Equal(110, zoom.Percent);

        zoom.Wheel(0);
        Assert.Equal(110, zoom.Percent);
    }

    [Fact]
    public void Changes_are_announced_for_the_scale_text_and_status_bar()
    {
        var zoom = new ZoomViewModel(new MemorySettings());
        var changed = new List<string?>();
        zoom.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        zoom.ZoomInCommand.Execute(null);

        Assert.Contains(nameof(ZoomViewModel.Scale), changed);
        Assert.Contains(nameof(ZoomViewModel.Text), changed);
        Assert.Contains(nameof(ZoomViewModel.IsScaled), changed);
    }

    [Fact]
    public void A_change_is_saved_and_starting_up_does_not_save()
    {
        var settings = new MemorySettings();
        var zoom = new ZoomViewModel(settings);
        Assert.Equal(0, settings.Saves);

        zoom.ZoomInCommand.Execute(null);

        Assert.Equal(1, settings.Saves);
        Assert.Equal(110, settings.ZoomPercent);
    }

    // ---- the file the setting lives in ----

    [Fact]
    public void The_file_store_remembers_the_level_between_runs()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");

        new FileSettingsStore(path).ZoomPercent = 150;

        Assert.Equal(150, new FileSettingsStore(path).ZoomPercent);
    }

    [Fact]
    public void A_missing_file_means_the_default()
    {
        Assert.Equal(100, new FileSettingsStore(Path.Combine(_dir, "none.json")).ZoomPercent);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"zoomPercent\":\"wide\"}")]
    public void A_damaged_file_means_the_default_not_a_crash(string content)
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, content);

        Assert.Equal(100, new FileSettingsStore(path).ZoomPercent);
    }

    [Fact]
    public void A_file_that_cannot_be_written_does_not_stop_the_setting_applying()
    {
        // The settings path is a folder, so writing fails.
        var path = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(path);
        var store = new FileSettingsStore(path);

        store.ZoomPercent = 140;

        Assert.Equal(140, store.ZoomPercent);
    }
}
