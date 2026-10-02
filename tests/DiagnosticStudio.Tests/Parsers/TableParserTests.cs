using System.Text;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers.Tables;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.Parsers;

public sealed class TableParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-table-" + Guid.NewGuid().ToString("N"));

    public TableParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Rec(string message, string time = "17:52:14.123+000", string date = "7-23-2026", string component = "AppWorkload", string type = "1", string thread = "17") =>
        $"<![LOG[{message}]LOG]!><time=\"{time}\" date=\"{date}\" component=\"{component}\" context=\"\" type=\"{type}\" thread=\"{thread}\" file=\"\">";

    private static CmTraceTable Cm(params string[] lines) => CmTraceTable.Build(new ListLines(lines));

    private static CsvTable Csv(params string[] lines) => CsvTable.Build(new ListLines(lines));

    // ======================= CMTrace =======================

    [Fact]
    public void Single_line_records_become_rows_with_time_level_component_thread_and_message()
    {
        var table = Cm(
            Rec("[Win32App] Processing started"),
            Rec("[Win32App] LogonUser failed with error 1326", time: "17:52:15.500-060", type: "3", thread: "42", component: "AppWorkload"),
            Rec("Retrying", type: "2", component: "Sensor"));

        Assert.Equal(3, table.RowCount);
        Assert.Equal(new[] { "Time", "Level", "Component", "Thread", "Message" }, table.Columns.Select(c => c.Name));

        var one = table.GetRow(0);
        Assert.Equal(new[] { "2026-07-23 17:52:14.123", "Information", "AppWorkload", "17", "[Win32App] Processing started" }, one.Cells);
        Assert.Equal(1, one.FirstLine);
        Assert.Equal(1, one.LineCount);

        var two = table.GetRow(1);
        Assert.Equal("2026-07-23 17:52:15.500", two.Cells[0]);   // the zone bias after the seconds is not part of the time
        Assert.Equal("Error", two.Cells[1]);
        Assert.Equal("42", two.Cells[3]);
        Assert.Equal("Warning", table.GetRow(2).Cells[1]);
    }

    [Fact]
    public void Severity_comes_from_the_records_type()
    {
        var table = Cm(Rec("a", type: "1"), Rec("b", type: "2"), Rec("c", type: "3"), Rec("d", type: "4"), Rec("e", type: ""));

        Assert.Equal(
            new[] { LogSeverity.Information, LogSeverity.Warning, LogSeverity.Error, LogSeverity.None, LogSeverity.None },
            Enumerable.Range(0, 5).Select(table.SeverityOf));
        Assert.True(table.HasLevels);
    }

    [Fact]
    public void A_message_that_runs_over_several_lines_is_one_record()
    {
        var table = Cm(
            Rec("first"),
            "<![LOG[Script output:",
            "  line two",
            "  line three]LOG]!><time=\"17:52:16.000+000\" date=\"7-23-2026\" component=\"Exec\" context=\"\" type=\"1\" thread=\"5\" file=\"\">",
            Rec("after"));

        Assert.Equal(3, table.RowCount);
        var multi = table.GetRow(1);
        Assert.Equal(2, multi.FirstLine);
        Assert.Equal(3, multi.LineCount);
        Assert.Equal(4, multi.LastLine);
        Assert.Equal("Script output: ↵   line two ↵   line three", multi.Cells[4]);
        Assert.Contains("Script output:\n  line two\n  line three", multi.Detail);
        Assert.Contains("Component: Exec", multi.Detail);
    }

    [Fact]
    public void Every_line_of_a_record_maps_back_to_it_and_lines_outside_records_do_not()
    {
        var table = Cm(
            "stray text before anything",
            Rec("one"),
            "<![LOG[two",
            "continued]LOG]!><time=\"17:52:16.000+000\" date=\"7-23-2026\" component=\"X\" context=\"\" type=\"1\" thread=\"5\" file=\"\">",
            "stray between",
            Rec("three"));

        Assert.Equal(-1, table.RowOfLine(1));
        Assert.Equal(0, table.RowOfLine(2));
        Assert.Equal(1, table.RowOfLine(3));
        Assert.Equal(1, table.RowOfLine(4));
        Assert.Equal(-1, table.RowOfLine(5));
        Assert.Equal(2, table.RowOfLine(6));
        Assert.Equal(-1, table.RowOfLine(99));
        Assert.Equal(-1, table.RowOfLine(0));
    }

    [Fact]
    public void Lines_that_belong_to_no_record_are_counted_with_the_first_one()
    {
        var table = Cm("header junk", Rec("a"), "more junk", "and more", Rec("b"));

        Assert.Equal(3, table.UnreadLines);
        Assert.Equal(1, table.FirstUnreadLine);
    }

    [Fact]
    public void A_record_that_never_closes_is_given_up_when_the_next_one_starts()
    {
        var table = Cm(
            Rec("good one"),
            "<![LOG[this one never closes",
            "still going",
            Rec("the next good one"));

        Assert.Equal(2, table.RowCount);
        Assert.Equal(new[] { 1, 4 }, new[] { table.GetRow(0).FirstLine, table.GetRow(1).FirstLine });
        Assert.Equal(2, table.UnreadLines);
        Assert.Equal(2, table.FirstUnreadLine);
    }

    [Fact]
    public void A_record_open_at_the_end_of_the_file_is_unread()
    {
        var table = Cm(Rec("fine"), "<![LOG[cut off here", "and here");

        Assert.Equal(1, table.RowCount);
        Assert.Equal(2, table.UnreadLines);
    }

    [Fact]
    public void A_runaway_record_cannot_swallow_the_file()
    {
        var lines = new List<string> { "<![LOG[opened and never closed" };
        lines.AddRange(Enumerable.Repeat("filler", CmTraceTable.MaxLinesPerRecord + 5));
        lines.Add(Rec("a real record afterwards"));

        var table = CmTraceTable.Build(new ListLines(lines));

        Assert.Equal(1, table.RowCount);
        Assert.Equal(lines.Count, table.GetRow(0).FirstLine);
        Assert.Equal(lines.Count - 1, table.UnreadLines);
    }

    [Fact]
    public void Attributes_in_any_order_or_missing_are_read_by_name()
    {
        var table = Cm(
            "<![LOG[reordered]LOG]!><thread=\"9\" component=\"Comp\" date=\"1-2-2026\" time=\"01:02:03.000+000\" type=\"2\">",
            "<![LOG[sparse]LOG]!><time=\"01:02:04.000+000\" date=\"1-2-2026\">");

        Assert.Equal(new[] { "2026-01-02 01:02:03.000", "Warning", "Comp", "9", "reordered" }, table.GetRow(0).Cells);
        Assert.Equal(new[] { "2026-01-02 01:02:04.000", string.Empty, string.Empty, string.Empty, "sparse" }, table.GetRow(1).Cells);
    }

    [Theory]
    [InlineData("7-23-2026", "17:52:14.123+000", "2026-07-23 17:52:14.123")]
    [InlineData("7/23/2026", "17:52:14.123+000", "2026-07-23 17:52:14.123")]
    [InlineData("12-1-2026", "09:05:07-300", "2026-12-01 09:05:07.000")]
    [InlineData("1-2-2026", "9:05:07.5+000", "2026-01-02 09:05:07.500")]
    public void Dates_and_times_are_shown_as_written_without_the_zone_suffix(string date, string time, string expected)
    {
        var table = Cm(Rec("x", time: time, date: date));

        Assert.Equal(expected, table.GetRow(0).Cells[0]);
    }

    [Fact]
    public void A_date_that_cannot_be_read_is_shown_as_it_is()
    {
        var table = Cm(Rec("x", time: "17:52:14.123+000", date: "not-a-date"));

        Assert.Equal("not-a-date 17:52:14.123", table.GetRow(0).Cells[0]);
    }

    [Fact]
    public void A_page_read_at_once_equals_the_rows_read_one_by_one()
    {
        var lines = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            lines.Add(i % 7 == 3 ? "<![LOG[multi " + i : Rec("row " + i));
            if (i % 7 == 3)
            {
                lines.Add("second line " + i + "]LOG]!><time=\"01:00:00.000+000\" date=\"1-1-2026\" component=\"C\" context=\"\" type=\"1\" thread=\"1\" file=\"\">");
            }
        }

        var table = CmTraceTable.Build(new ListLines(lines));
        var page = table.GetRows(5, 20);

        Assert.Equal(20, page.Count);
        for (var i = 0; i < page.Count; i++)
        {
            Assert.Equal(table.GetRow(5 + i).Cells, page[i].Cells);
            Assert.Equal(table.GetRow(5 + i).FirstLine, page[i].FirstLine);
        }

        Assert.Equal(table.RowCount - 45, table.GetRows(45, 100).Count); // fewer at the end
        Assert.Empty(table.GetRows(table.RowCount, 5));
    }

    [Fact]
    public void An_intune_style_line_with_brackets_and_colons_in_the_message_reads_cleanly()
    {
        var table = Cm(Rec("[Win32App] LogonUser failed with error 1326: The user name or password is incorrect. [Config: a=b]", type: "3"));

        Assert.Equal("[Win32App] LogonUser failed with error 1326: The user name or password is incorrect. [Config: a=b]", table.GetRow(0).Cells[4]);
    }

    [Fact]
    public void A_file_with_no_records_has_no_rows()
    {
        var table = Cm("just a plain log", "with no markers");

        Assert.Equal(0, table.RowCount);
        Assert.Equal(2, table.UnreadLines);
    }

    [Theory]
    [InlineData("2026 plain log line\nanother", false)]
    [InlineData("<![LOG[opened but no end marker in the sample", false)]
    [InlineData("<![LOG[ok]LOG]!><time=\"1\">", true)]
    [InlineData("junk first\n<![LOG[a]LOG]!>", true)]
    public void A_file_is_recognised_by_its_record_markers(string text, bool expected)
    {
        Assert.Equal(expected, CmTraceTable.LooksLikeCmTrace(text));
    }

    // ---- through the parser, on real files ----

    private DiagnosticArtifact FileArtifact(string name, ArtifactType type, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = type,
        };
    }

    [Fact]
    public async Task The_parser_opens_a_cmtrace_log_as_a_table_with_its_raw_text()
    {
        var content = Encoding.UTF8.GetBytes(Rec("one") + "\r\n" + Rec("two", type: "3") + "\r\n");
        var artifact = FileArtifact("IntuneManagementExtension.log", ArtifactType.TextLog, content);
        var parser = new CmTraceParser();

        Assert.True(parser.CanHandle(artifact));
        var document = Assert.IsType<TableDocument>(await parser.ParseAsync(artifact, CancellationToken.None));

        Assert.Equal(TableFormat.CmTrace, document.Format);
        Assert.Equal(2, document.Table.RowCount);
        Assert.Equal(2, document.RawSource.LineCount);
        Assert.Equal(0, document.UnreadLines);
        Assert.Equal("Error", document.Table.GetRow(1).Cells[1]);
    }

    [Fact]
    public async Task A_utf16_cmtrace_log_is_recognised_and_read()
    {
        var content = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Rec("wide") + "\r\n")).ToArray();
        var artifact = FileArtifact("wide.log", ArtifactType.TextLog, content);

        Assert.True(new CmTraceParser().CanHandle(artifact));
        var document = (TableDocument)await new CmTraceParser().ParseAsync(artifact, CancellationToken.None);

        Assert.Equal("wide", document.Table.GetRow(0).Cells[4]);
    }

    [Fact]
    public void An_ordinary_log_is_left_to_the_text_parser()
    {
        var artifact = FileArtifact("plain.log", ArtifactType.TextLog, Encoding.UTF8.GetBytes("2026-07-23 14:12:00 INFO hello\n2026-07-23 14:12:01 ERROR boom\n"));

        Assert.False(new CmTraceParser().CanHandle(artifact));
    }

    [Fact]
    public void Only_text_artifacts_are_considered_and_a_missing_file_is_not_a_crash()
    {
        var reg = FileArtifact("k.reg", ArtifactType.RegistryExport, Encoding.UTF8.GetBytes(Rec("x")));
        var gone = reg with { Id = Guid.NewGuid(), ArtifactType = ArtifactType.TextLog, ExtractedPath = Path.Combine(_dir, "missing.log") };
        var noPath = gone with { ExtractedPath = null };

        Assert.False(new CmTraceParser().CanHandle(reg));
        Assert.False(new CmTraceParser().CanHandle(gone));
        Assert.False(new CmTraceParser().CanHandle(noPath));
    }

    // ======================= CSV =======================

    [Fact]
    public void A_header_row_names_the_columns_and_the_rest_are_records()
    {
        var table = Csv("Name,Count,When", "alpha,1,2026-07-23 14:12:00", "beta,22,2026-07-23 14:12:01");

        Assert.True(table.HasHeader);
        Assert.Equal(',', table.Delimiter);
        Assert.Equal(new[] { "Name", "Count", "When" }, table.Columns.Select(c => c.Name));
        Assert.Equal(new[] { TableColumnKind.Text, TableColumnKind.Number, TableColumnKind.Time }, table.Columns.Select(c => c.Kind));
        Assert.Equal(2, table.RowCount);
        Assert.Equal(1, table.HeaderLines);
        Assert.Equal(new[] { "alpha", "1", "2026-07-23 14:12:00" }, table.GetRow(0).Cells);
        Assert.Equal(2, table.GetRow(0).FirstLine);
    }

    [Fact]
    public void Quoted_values_may_hold_the_delimiter_quotes_and_line_breaks()
    {
        var table = Csv(
            "id,note",
            "1,\"has, a comma\"",
            "2,\"says \"\"hi\"\"\"",
            "3,\"two",
            "lines\"",
            "4,plain");

        Assert.Equal(4, table.RowCount);
        Assert.Equal("has, a comma", table.GetRow(0).Cells[1]);
        Assert.Equal("says \"hi\"", table.GetRow(1).Cells[1]);

        var multi = table.GetRow(2);
        Assert.Equal(4, multi.FirstLine);
        Assert.Equal(2, multi.LineCount);
        Assert.Equal("two ↵ lines", multi.Cells[1]);
        Assert.Contains("note: two\nlines", multi.Detail);

        Assert.Equal("plain", table.GetRow(3).Cells[1]);
        Assert.Equal(2, table.RowOfLine(5)); // the second line of the multi-line record
        Assert.Equal(-1, table.RowOfLine(1)); // the header
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3\n4;5;6", ';', "semicolon")]
    [InlineData("a\tb\tc\n1\t2\t3\n4\t5\t6", '\t', "tab")]
    [InlineData("a|b|c\n1|2|3\n4|5|6", '|', "pipe")]
    [InlineData("a,b,c\n1,2,3\n4,5,6", ',', "comma")]
    public void The_delimiter_is_found_from_the_first_lines(string text, char expected, string name)
    {
        var table = Csv(text.Split('\n'));

        Assert.Equal(expected, table.Delimiter);
        Assert.Equal(name, table.DelimiterName);
        Assert.Equal(3, table.Columns.Count);
    }

    [Fact]
    public void Delimiters_inside_quotes_do_not_count_when_choosing()
    {
        var table = Csv("name;note", "a;\"x,y,z,w\"", "b;\"p,q,r,s\"");

        Assert.Equal(';', table.Delimiter);
        Assert.Equal("x,y,z,w", table.GetRow(0).Cells[1]);
    }

    [Fact]
    public void A_file_whose_first_row_is_data_gets_numbered_columns()
    {
        var table = Csv("1,2,3", "4,5,6");

        Assert.False(table.HasHeader);
        Assert.Equal(new[] { "Column 1", "Column 2", "Column 3" }, table.Columns.Select(c => c.Name));
        Assert.Equal(2, table.RowCount);
        Assert.Equal(1, table.GetRow(0).FirstLine);
        Assert.Equal(0, table.HeaderLines);
    }

    [Fact]
    public void Repeated_column_names_are_made_unique_and_blank_ones_numbered()
    {
        var table = Csv("Name,Name,,Value", "a,b,c,1");

        Assert.Equal(new[] { "Name", "Name_2", "Column 3", "Value" }, table.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Blank_lines_between_records_are_not_records()
    {
        var table = Csv("a,b", "1,2", string.Empty, "  ", "3,4", string.Empty);

        Assert.Equal(2, table.RowCount);
        Assert.Equal(5, table.GetRow(1).FirstLine);
    }

    [Fact]
    public void Rows_with_more_or_fewer_values_than_the_header_still_show_everything()
    {
        var table = Csv("a,b,c", "1,2", "1,2,3,4,5");

        // The longest record decides how many columns there are; shorter ones are padded.
        Assert.Equal(5, table.Columns.Count);
        Assert.Equal(new[] { "1", "2", string.Empty, string.Empty, string.Empty }, table.GetRow(0).Cells);
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, table.GetRow(1).Cells);
        Assert.Contains("Column 4: 4", table.GetRow(1).Detail);
    }

    [Fact]
    public void An_empty_file_is_a_table_with_no_rows()
    {
        var table = Csv();

        Assert.Equal(0, table.RowCount);
        Assert.Single(table.Columns);
    }

    [Fact]
    public void A_header_only_file_has_columns_and_no_rows()
    {
        var table = Csv("Name,Count,When");

        Assert.Equal(0, table.RowCount);
        Assert.Equal(new[] { "Name", "Count", "When" }, table.Columns.Select(c => c.Name));
    }

    [Fact]
    public void A_quote_that_never_closes_makes_the_rest_one_record_and_says_so()
    {
        var table = Csv("a,b", "1,2", "3,\"open", "4,5", "6,7");

        Assert.Equal(2, table.RowCount);
        Assert.Equal(1, table.UnreadLines);
        Assert.Equal(3, table.FirstUnreadLine);
        Assert.Equal(3, table.GetRow(1).FirstLine);
        Assert.Equal(3, table.GetRow(1).LineCount);
    }

    [Fact]
    public void A_runaway_quote_cannot_swallow_the_file()
    {
        var lines = new List<string> { "a,b", "1,\"open and never closed" };
        lines.AddRange(Enumerable.Repeat("filler,filler", CsvTable.MaxLinesPerRecord + 5));
        lines.Add("2,3");

        var table = CsvTable.Build(new ListLines(lines));

        // The first line of the runaway is a record of its own and reading carries on after it, so the file still
        // yields rows rather than one enormous one.
        Assert.True(table.RowCount > 2);
        Assert.True(table.UnreadLines >= 1);
    }

    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData("a,,c", new[] { "a", "", "c" })]
    [InlineData("a,b,", new[] { "a", "b", "" })]
    [InlineData("\"a,b\",c", new[] { "a,b", "c" })]
    [InlineData("\"q\"\"q\",c", new[] { "q\"q", "c" })]
    [InlineData("5\" pipe,c", new[] { "5\" pipe", "c" })]
    [InlineData("", new[] { "" })]
    [InlineData("\"\",x", new[] { "", "x" })]
    public void Splitting_a_record_follows_the_csv_rules(string line, string[] expected)
    {
        Assert.Equal(expected, CsvTable.SplitFields(line, ','));
    }

    [Fact]
    public void Column_widths_follow_the_longest_value_within_limits()
    {
        var table = Csv("short,long", "a," + new string('x', 200), "b,y");

        Assert.True(table.Columns[0].Width >= 70);
        Assert.Equal(420, table.Columns[1].Width);
    }

    [Fact]
    public void A_page_of_rows_equals_the_rows_one_by_one_across_multi_line_records()
    {
        var lines = new List<string> { "id,note" };
        for (var i = 0; i < 40; i++)
        {
            lines.Add(i % 5 == 2 ? i + ",\"line a" : i + ",plain " + i);
            if (i % 5 == 2)
            {
                lines.Add("line b\"");
            }
        }

        var table = CsvTable.Build(new ListLines(lines));
        var page = table.GetRows(3, 25);

        for (var i = 0; i < page.Count; i++)
        {
            Assert.Equal(table.GetRow(3 + i).Cells, page[i].Cells);
        }
    }

    // ---- through the parser ----

    [Fact]
    public async Task The_parser_opens_a_csv_file_as_a_table_and_notes_what_it_assumed()
    {
        var artifact = FileArtifact("report.csv", ArtifactType.Csv, Encoding.UTF8.GetBytes("Name;Value\r\nalpha;1\r\nbeta;2\r\n"));
        var parser = new CsvParser();

        Assert.True(parser.CanHandle(artifact));
        var document = Assert.IsType<TableDocument>(await parser.ParseAsync(artifact, CancellationToken.None));

        Assert.Equal(TableFormat.Csv, document.Format);
        Assert.Equal(2, document.Table.RowCount);
        Assert.Contains("semicolon", document.Note);
        Assert.Contains("column names", document.Note);
    }

    [Fact]
    public async Task A_byte_order_mark_does_not_end_up_in_the_first_column_name()
    {
        var content = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("Name,Value\r\nalpha,1\r\n")).ToArray();
        var artifact = FileArtifact("bom.csv", ArtifactType.Csv, content);

        var document = (TableDocument)await new CsvParser().ParseAsync(artifact, CancellationToken.None);

        Assert.Equal("Name", document.Table.Columns[0].Name);
    }

    [Fact]
    public void The_csv_parser_only_takes_csv_artifacts_with_a_file()
    {
        var csv = FileArtifact("a.csv", ArtifactType.Csv, new byte[] { 65 });

        Assert.False(new CsvParser().CanHandle(csv with { ArtifactType = ArtifactType.TextLog }));
        Assert.False(new CsvParser().CanHandle(csv with { ExtractedPath = null }));
    }

    // ---- shared helpers ----

    [Fact]
    public void The_text_of_a_table_is_its_raw_source_and_non_text_documents_have_none()
    {
        var table = Cm(Rec("x"));
        var artifact = Artifact("a.log", ArtifactType.TextLog);
        var doc = new TableDocument { Artifact = artifact, Format = TableFormat.CmTrace, Table = table, RawSource = new ListLines(new[] { "raw" }) };

        Assert.Same(doc.RawSource, DocumentText.LinesOf(doc));
        Assert.NotNull(DocumentText.LinesOf(Text(artifact, "a")));
        Assert.Null(DocumentText.LinesOf(new UnsupportedDocument { Artifact = artifact, Reason = "r" }));
    }
}
