using System.Text;
using Bazaar.Server.DevSandbox.Windows;

namespace Bazaar.Server.DevSandbox.Windows.Tests;

public sealed class WindowsCommandLineTests
{
    /// <summary>Reference parser: the C runtime / CommandLineToArgvW rules for arguments after the program name.</summary>
    private static List<string> Parse(string commandLine)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        var inArgument = false;
        var inQuotes = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    n++;
                    i++;
                }

                inArgument = true;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    sb.Append('\\', n / 2);
                    if (n % 2 == 1)
                    {
                        sb.Append('"');
                        i++;
                    }
                }
                else
                {
                    sb.Append('\\', n);
                }

                continue;
            }

            if (c == '"')
            {
                inArgument = true;
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    continue;
                }

                inQuotes = !inQuotes;
                i++;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (inArgument)
                {
                    args.Add(sb.ToString());
                    sb.Clear();
                    inArgument = false;
                }

                i++;
                continue;
            }

            inArgument = true;
            sb.Append(c);
            i++;
        }

        if (inArgument)
        {
            args.Add(sb.ToString());
        }

        return args;
    }

    public static TheoryData<string, string> Vectors => new()
    {
        { "", "\"\"" },
        { "abc", "abc" },
        { "a b", "\"a b\"" },
        { "a\tb", "\"a\tb\"" },
        { "a\"b", "\"a\\\"b\"" },
        { "\"", "\"\\\"\"" },
        { "\\", "\\" },
        { "a\\b", "a\\b" },
        { "a\\\\b", "a\\\\b" },
        { "a b\\", "\"a b\\\\\"" },
        { "a b\\\\", "\"a b\\\\\\\\\"" },
        { "\\\"", "\"\\\\\\\"\"" },
        { "\\\\\"", "\"\\\\\\\\\\\"\"" },
        { "C:\\Program Files\\x\\", "\"C:\\Program Files\\x\\\\\"" },
        { "C:\\dir\\file.txt", "C:\\dir\\file.txt" },
        { "say \"hi\" now", "\"say \\\"hi\\\" now\"" },
        { "a&b|c>d", "a&b|c>d" },
        { "%PATH%", "%PATH%" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Quote_matches_the_expected_vector_and_parses_back(string argument, string expected)
    {
        var quoted = WindowsCommandLine.Quote(argument);
        Assert.Equal(expected, quoted);
        Assert.Equal([argument], Parse(quoted));
    }

    [Fact]
    public void Every_string_over_the_awkward_alphabet_round_trips_alone_and_in_a_vector()
    {
        char[] alphabet = ['a', ' ', '\\', '"', '\t'];
        var all = new List<string> { string.Empty };
        var frontier = new List<string> { string.Empty };
        for (var length = 1; length <= 6; length++)
        {
            frontier = frontier.SelectMany(s => alphabet.Select(c => s + c)).ToList();
            all.AddRange(frontier);
        }

        Assert.Equal(1 + 5 + 25 + 125 + 625 + 3125 + 15625, all.Count);
        foreach (var s in all)
        {
            Assert.Equal([s], Parse(WindowsCommandLine.Quote(s)));
        }

        // Neighbouring arguments must not bleed into each other.
        for (var i = 0; i < all.Count; i += 7)
        {
            var a = all[i];
            var b = all[(i * 31 + 5) % all.Count];
            var line = WindowsCommandLine.Quote(a) + " " + WindowsCommandLine.Quote(b);
            Assert.Equal([a, b], Parse(line));
        }
    }

    [Fact]
    public void Build_quotes_the_program_and_each_argument()
    {
        var line = WindowsCommandLine.Build("C:\\Program Files\\App\\app.exe", ["one", "two words", "q\"uote", "tail\\", string.Empty]);
        Assert.Equal("\"C:\\Program Files\\App\\app.exe\" one \"two words\" \"q\\\"uote\" tail\\ \"\"", line);

        var parsed = Parse(line);
        Assert.Equal(["C:\\Program Files\\App\\app.exe", "one", "two words", "q\"uote", "tail\\", string.Empty], parsed);
    }

    [Fact]
    public void Build_with_no_arguments_is_the_quoted_program_only()
    {
        Assert.Equal("\"C:\\a.exe\"", WindowsCommandLine.Build("C:\\a.exe", []));
    }

    [Fact]
    public void Nul_in_an_argument_or_a_quote_in_the_program_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Quote("a\0b"));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a\"b.exe", []));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a\0.exe", []));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build(string.Empty, []));
    }

    [Fact]
    public void Environment_block_is_sorted_ignoring_case_nul_separated_and_double_terminated()
    {
        var block = WindowsEnvironmentBlock.Build(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1", ["C"] = "3" });
        Assert.Equal("A=1\0b=2\0C=3\0\0", block);
    }

    [Fact]
    public void Empty_environment_block_is_two_nuls()
    {
        Assert.Equal("\0\0", WindowsEnvironmentBlock.Build(new Dictionary<string, string>()));
    }

    [Fact]
    public void Run_environment_has_only_system_root_and_the_work_directory_as_temp()
    {
        var block = WindowsEnvironmentBlock.ForRun("C:\\Windows", "C:\\w\\run");
        Assert.Equal("LOCALAPPDATA=C:\\w\\run\0SystemRoot=C:\\Windows\0TEMP=C:\\w\\run\0TMP=C:\\w\\run\0\0", block);
        Assert.DoesNotContain("PATH", block, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", "v")]
    [InlineData("a=b", "v")]
    [InlineData("a\0b", "v")]
    [InlineData("a", "v\0w")]
    public void Environment_block_refuses_bad_names_and_values(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => WindowsEnvironmentBlock.Build(new Dictionary<string, string> { [name] = value }));
    }
}
