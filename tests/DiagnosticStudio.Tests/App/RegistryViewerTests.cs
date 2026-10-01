using DiagnosticStudio.App.ViewModels.RegistryViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.App;

public sealed class RegistryViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-regvm-" + Guid.NewGuid().ToString("N"));

    public RegistryViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Sample = """
        Windows Registry Editor Version 5.00

        [HKEY_CURRENT_USER\Software\Contoso]
        "Setting"="on"

        [HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate]
        "WUServer"="http://wsus.contoso.test:8530"
        @="default au"

        [HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU]
        "NoAutoUpdate"=dword:00000001
        "Blob"=hex:01,02,03,04
        "Multi"=hex(7):61,00,00,00,62,00,00,00,00,00

        [HKEY_LOCAL_MACHINE\SYSTEM\Zeta]
        "Z"="last"
        """;

    private (RegistryViewerViewModel Vm, RegistryDocument Doc) Open(string text = Sample)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".reg");
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        var artifact = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = "t.reg",
            OriginalPath = "t.reg",
            ExtractedPath = path,
            Provenance = new[] { "Bundle", "t.reg" },
            ArtifactType = ArtifactType.RegistryExport,
        };
        var doc = (RegistryDocument)new RegFileParser().ParseAsync(artifact, CancellationToken.None).GetAwaiter().GetResult();
        return (new RegistryViewerViewModel(doc), doc);
    }

    private static async Task Find(RegistryViewerViewModel vm, string text)
    {
        vm.FindText = text;
        await vm.PendingSearch;
    }

    // ---- tree ----

    [Fact]
    public void Hives_are_listed_alphabetically_and_the_first_is_selected_on_open()
    {
        var (vm, _) = Open();

        Assert.Equal(new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" }, vm.Roots.Select(r => r.Key!.Name));
        Assert.Equal("HKEY_CURRENT_USER", vm.SelectedKeyPath);
        Assert.True(vm.Roots[0].IsExpanded);
    }

    [Fact]
    public void Children_are_created_lazily_on_first_expansion()
    {
        var (vm, _) = Open();
        var hklm = vm.Roots[1];

        Assert.Single(hklm.Children);
        Assert.True(hklm.Children[0].IsPlaceholder);

        hklm.IsExpanded = true;

        Assert.Equal(new[] { "SOFTWARE", "SYSTEM" }, hklm.Children.Select(c => c.Key!.Name));
        Assert.All(hklm.Children, c => Assert.False(c.IsPlaceholder));
    }

    [Fact]
    public void Leaf_keys_have_no_placeholder_child()
    {
        var (vm, doc) = Open();
        var leaf = new RegistryKeyNodeViewModel(doc.FindKey(@"HKEY_LOCAL_MACHINE\SYSTEM\Zeta"), null);

        Assert.Empty(leaf.Children);
    }

    [Fact]
    public void Selecting_a_key_shows_its_values_and_path()
    {
        var (vm, doc) = Open();

        vm.RevealKey(doc.FindKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")!);

        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", vm.SelectedKeyPath);
        Assert.Equal(new[] { "WUServer", "" }, vm.Values.Select(v => v.Name));
        Assert.Null(vm.SelectedValue);
    }

    // ---- detail ----

    [Fact]
    public void Detail_shows_hex_dump_for_binary_and_one_item_per_line_for_multi_string()
    {
        var (vm, doc) = Open();
        vm.RevealKey(doc.FindKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU")!);

        vm.SelectedValue = vm.Values.Single(v => v.Name == "Blob");
        Assert.StartsWith("00000000  01 02 03 04", vm.DetailText);

        vm.SelectedValue = vm.Values.Single(v => v.Name == "Multi");
        Assert.Equal("a" + Environment.NewLine + "b", vm.DetailText);

        vm.SelectedValue = vm.Values.Single(v => v.Name == "NoAutoUpdate");
        Assert.Equal("0x00000001 (1)", vm.DetailText);

        vm.SelectedValue = null;
        Assert.Equal(string.Empty, vm.DetailText);
    }

    [Fact]
    public void Hex_dump_notes_truncation_of_large_values()
    {
        var bytes = string.Join(",", Enumerable.Range(0, 5000).Select(i => (i % 256).ToString("x2")));
        var (vm, doc) = Open($"Windows Registry Editor Version 5.00\n\n[HKEY_CURRENT_USER\\K]\n\"Big\"=hex:{bytes}\n");
        vm.RevealKey(doc.FindKey(@"HKEY_CURRENT_USER\K")!);

        vm.SelectedValue = vm.Values.Single();

        Assert.Contains("first 4,096 of 5,000 bytes", vm.DetailText);
    }

    // ---- location navigation ----

    [Fact]
    public void Navigating_to_a_key_location_expands_and_selects_it_on_the_registry_tab()
    {
        var (vm, doc) = Open();
        vm.SelectedTabIndex = RegistryViewerViewModel.RawTab;
        var id = doc.Artifact.Id;

        var ok = vm.NavigateTo(DiagnosticLocation.ForRegistry(id, @"hkey_local_machine\software\policies\microsoft\windows\windowsupdate\au"));

        Assert.True(ok);
        Assert.Equal(RegistryViewerViewModel.RegistryTab, vm.SelectedTabIndex);
        Assert.EndsWith(@"WindowsUpdate\AU", vm.SelectedKeyPath);
        Assert.True(vm.SelectedKey!.IsSelected);
        Assert.Null(vm.SelectedValue);

        // Every ancestor on the way down is now expanded.
        var node = vm.Roots[1];
        foreach (var segment in new[] { "SOFTWARE", "Policies", "Microsoft", "Windows", "WindowsUpdate" })
        {
            Assert.True(node.IsExpanded, node.DisplayName);
            node = node.Children.Single(c => c.Key!.Name == segment);
        }
    }

    [Fact]
    public void Navigating_to_a_value_location_selects_the_value_including_the_default_value()
    {
        var (vm, doc) = Open();
        var id = doc.Artifact.Id;
        var revealed = 0;
        vm.ValueRevealRequested += (_, _) => revealed++;
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

        Assert.True(vm.NavigateTo(DiagnosticLocation.ForRegistry(id, key, "WUServer")));
        Assert.Equal("WUServer", vm.SelectedValue!.Name);

        Assert.True(vm.NavigateTo(DiagnosticLocation.ForRegistry(id, key, "")));
        Assert.True(vm.SelectedValue!.IsDefault);
        Assert.Equal("default au", vm.DetailText);
        Assert.Equal(2, revealed);
    }

    [Fact]
    public void Unresolvable_registry_locations_return_false()
    {
        var (vm, doc) = Open();
        var id = doc.Artifact.Id;

        Assert.False(vm.NavigateTo(DiagnosticLocation.ForRegistry(id, @"HKEY_LOCAL_MACHINE\Nope")));
        Assert.False(vm.NavigateTo(DiagnosticLocation.ForRegistry(id, @"HKEY_CURRENT_USER\Software\Contoso", "NoSuchValue")));
        Assert.False(vm.NavigateTo(DiagnosticLocation.ForArtifact(id)));
    }

    [Fact]
    public void Navigating_to_a_line_location_opens_the_raw_source_at_that_line()
    {
        var (vm, doc) = Open();

        Assert.True(vm.NavigateTo(DiagnosticLocation.ForLine(doc.Artifact.Id, 7)));

        Assert.Equal(RegistryViewerViewModel.RawTab, vm.SelectedTabIndex);
        Assert.Equal(7, vm.Raw.CurrentLine);
    }

    [Fact]
    public void Show_in_raw_source_goes_to_the_selected_values_line_or_the_key_header()
    {
        var (vm, doc) = Open();
        vm.RevealKey(doc.FindKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")!);
        var wuLine = vm.Values.Single(v => v.Name == "WUServer").SourceLine;
        vm.SelectedValue = vm.Values.Single(v => v.Name == "WUServer");

        vm.ShowInRawSourceCommand.Execute(null);

        Assert.Equal(RegistryViewerViewModel.RawTab, vm.SelectedTabIndex);
        Assert.Equal(wuLine, vm.Raw.CurrentLine);
        Assert.Equal("\"WUServer\"=\"http://wsus.contoso.test:8530\"", vm.Raw.Lines[wuLine - 1].Text);

        vm.SelectedTabIndex = RegistryViewerViewModel.RegistryTab;
        vm.SelectedValue = null;
        vm.ShowInRawSourceCommand.Execute(null);
        Assert.Equal(vm.SelectedKey!.Key!.SourceLine, vm.Raw.CurrentLine);
    }

    // ---- find ----

    [Fact]
    public async Task Find_selects_the_first_match_after_the_current_selection_and_steps_through_matches()
    {
        var (vm, _) = Open();

        await Find(vm, "update");

        // The WindowsUpdate key (by name) and the NoAutoUpdate value (by name).
        Assert.Equal(2, vm.MatchCount);
        Assert.Equal("1 of 2", vm.SearchStatus);
        Assert.EndsWith("WindowsUpdate", vm.SelectedKeyPath);
        Assert.Null(vm.SelectedValue);

        vm.FindNextCommand.Execute(null);
        Assert.Equal("2 of 2", vm.SearchStatus);
        Assert.Equal("NoAutoUpdate", vm.SelectedValue!.Name);
    }

    [Fact]
    public async Task Find_next_and_previous_wrap_and_select_the_value_row()
    {
        var (vm, _) = Open();
        await Find(vm, "contoso"); // the Contoso key and the WUServer data
        Assert.Equal(2, vm.MatchCount);

        var visited = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            visited.Add(vm.SearchStatus);
            vm.FindNextCommand.Execute(null);
        }

        Assert.Equal(new[] { "1 of 2", "2 of 2", "1 of 2" }, visited);

        // After three Next presses the cursor is on match 2; Previous goes to 1 and then wraps to the last.
        vm.FindPreviousCommand.Execute(null);
        Assert.Equal("1 of 2", vm.SearchStatus);
        vm.FindPreviousCommand.Execute(null);
        Assert.Equal("2 of 2", vm.SearchStatus);
        Assert.Equal("WUServer", vm.SelectedValue!.Name);
    }

    [Fact]
    public async Task Value_data_match_selects_key_and_value()
    {
        var (vm, _) = Open();

        await Find(vm, "wsus.contoso");

        Assert.Equal(1, vm.MatchCount);
        Assert.Equal("WUServer", vm.SelectedValue!.Name);
        Assert.EndsWith("WindowsUpdate", vm.SelectedKeyPath);
    }

    [Fact]
    public async Task Case_toggle_no_match_and_clear()
    {
        var (vm, _) = Open();
        await Find(vm, "WUSERVER");
        Assert.Equal(1, vm.MatchCount);

        vm.MatchCase = true;
        await vm.PendingSearch;
        Assert.Equal(0, vm.MatchCount);
        Assert.Equal("No matches", vm.SearchStatus);

        await Find(vm, "");
        Assert.Equal(string.Empty, vm.SearchStatus);
    }

    [Fact]
    public async Task Find_commands_with_no_matches_do_nothing()
    {
        var (vm, _) = Open();
        await Find(vm, "zzzzzz");
        var before = vm.SelectedKeyPath;

        vm.FindNextCommand.Execute(null);
        vm.FindPreviousCommand.Execute(null);

        Assert.Equal(before, vm.SelectedKeyPath);
    }

    // ---- info / warnings ----

    [Fact]
    public void Info_text_summarises_the_file_and_clean_files_have_no_warning()
    {
        var (vm, _) = Open();

        Assert.Contains("Windows Registry Editor Version 5.00", vm.InfoText);
        Assert.Contains("keys", vm.InfoText);
        Assert.Contains("values", vm.InfoText);
        Assert.Null(vm.WarningText);
    }

    [Fact]
    public void Parse_problems_are_called_out_and_point_to_the_raw_source()
    {
        var (vm, _) = Open("Windows Registry Editor Version 5.00\n\n[HKEY_CURRENT_USER\\K]\nnonsense\n\"A\"=\"1\"\n");

        Assert.Contains("1 lines could not be parsed", vm.WarningText);
        Assert.Contains("line 4", vm.WarningText);
        Assert.Contains("raw source", vm.WarningText);
    }

    [Fact]
    public void An_empty_or_headerless_file_still_opens_with_a_raw_tab()
    {
        var (vm, _) = Open("");

        Assert.Empty(vm.Roots);
        Assert.Contains("No .reg header", vm.InfoText);
        Assert.Empty(vm.Raw.Lines);
    }
}
