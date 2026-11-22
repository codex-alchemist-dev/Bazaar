using Bazaar.Client.Calcinato.Pty;

namespace Bazaar.Client.Calcinato.Pty.Tests;

public sealed class ShellResolverTests
{
    private static Func<string, string?> OnPath(params string[] present) =>
        name => present.Contains(name, StringComparer.Ordinal) ? "/x/" + name : null;

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(pair => pair.Name == name).Value;

    [Fact]
    public void Requested_name_found_on_path_is_used()
    {
        var shell = ShellResolver.Resolve("zsh", false, Env(), OnPath("zsh", "bash"));

        Assert.Equal("/x/zsh", shell);
    }

    [Fact]
    public void Requested_shell_that_does_not_exist_throws_instead_of_falling_back()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ShellResolver.Resolve("fish", false, Env(), OnPath("bash")));
    }

    [Fact]
    public void Requested_existing_file_path_is_used_as_is()
    {
        var file = TempFile();
        try
        {
            var shell = ShellResolver.Resolve(file, false, Env(), OnPath("bash"));

            Assert.Equal(Path_GetFullPath(file), shell);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Shell_environment_variable_is_used_before_the_defaults()
    {
        var file = TempFile();
        try
        {
            var shell = ShellResolver.Resolve(null, false, Env(("SHELL", file)), OnPath("bash"));

            Assert.Equal(Path_GetFullPath(file), shell);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Missing_shell_environment_variable_falls_back_to_the_default()
    {
        var shell = ShellResolver.Resolve(null, false, Env(("SHELL", "/does/not/exist")), OnPath("bash", "sh"));

        Assert.Equal("/x/bash", shell);
    }

    [Fact]
    public void Linux_default_prefers_bash_then_sh()
    {
        Assert.Equal("/x/bash", ShellResolver.Resolve(null, false, Env(), OnPath("bash", "sh")));
        Assert.Equal("/x/sh", ShellResolver.Resolve(null, false, Env(), OnPath("sh")));
    }

    [Fact]
    public void Windows_default_prefers_pwsh_then_cmd()
    {
        Assert.Equal("/x/pwsh", ShellResolver.Resolve(null, true, Env(), OnPath("pwsh", "cmd")));
        Assert.Equal("/x/cmd", ShellResolver.Resolve(null, true, Env(), OnPath("cmd")));
    }

    [Fact]
    public void No_shell_found_throws()
    {
        Assert.Throws<InvalidOperationException>(() => ShellResolver.Resolve(null, false, Env(), OnPath()));
    }

    [Fact]
    public void Real_path_search_finds_a_shell_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The PATH search on disk is checked on Linux only.");
        }

        var shell = ShellResolver.Resolve(null, isWindows: false);

        Assert.True(File.Exists(shell), shell);
    }

    private static string TempFile()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "calcinato-shell-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "#!/bin/sh\n");
        return file;
    }

    private static string Path_GetFullPath(string path) => System.IO.Path.GetFullPath(path);
}
