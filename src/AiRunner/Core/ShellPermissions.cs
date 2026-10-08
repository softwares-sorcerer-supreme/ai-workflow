using System.Text.RegularExpressions;

namespace AiRunner.Core;

/// <summary>
/// Copilot shell permissions for the repo's setup, build, test and lint commands. In "copilot -p"
/// nobody approves a command, so a pattern that is too narrow ("shell(dotnet build X.sln)") silently
/// blocks "dotnet build X.sln -c Release" or "dotnet test --filter ..." and the agent skips the check.
/// So the agent gets the whole tool ("shell(dotnet:*)"), like a developer at the keyboard.
/// Shells and launchers keep the exact command, else they would allow every command. Never git.
/// </summary>
public static class ShellPermissions
{
    // Longer first: Split takes the first separator in this list that matches at a position.
    private static readonly string[] Separators = ["&&", "||", "&", "|", ";", "\n"];

    // A word, or a quoted path with spaces ("C:\Program Files\x\tool.exe").
    private static readonly Regex Word = new(@"""[^""]*""|'[^']*'|\S+");

    /// <summary>Tools that run any other command; for these only the exact command is allowed.</summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe", "sh", "bash", "zsh",
        "env", "sudo", "xargs", "eval", "exec", "call", "start",
    };

    public static IEnumerable<string> For(string command) =>
        command.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Pattern)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    private static string? Pattern(string part)
    {
        // Skip leading "NAME=value" assignments ("CI=1 npm test").
        var words = Word.Matches(part).Select(m => m.Value)
            .SkipWhile(w => w.Contains('=') && !w.StartsWith('-'))
            .ToArray();
        if (words.Length == 0) return null;

        var tool = words[0].Trim('"', '\'');
        var name = Path.GetFileNameWithoutExtension(tool);
        if (name.Equals("git", StringComparison.OrdinalIgnoreCase)) return null;
        return Launchers.Contains(tool) || Launchers.Contains(name)
            ? $"shell({string.Join(' ', words)})"
            : $"shell({tool}:*)";
    }
}
