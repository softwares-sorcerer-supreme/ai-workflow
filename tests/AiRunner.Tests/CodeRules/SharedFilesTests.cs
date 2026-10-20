using AiRunner.Infrastructure;
using AiRunner.Tests.Fakes;

namespace AiRunner.Tests.CodeRules;

/// <summary>
/// The shared files of the runner (agents, skills, templates, onboarding, the docs that guide agents) must work for
/// every project: no stack, framework, design-system file, repo or project of a product may be named there. Stack
/// facts live in the product repos' instruction files and in repos.yml; examples live under onboarding/examples/.
/// </summary>
public class SharedFilesTests
{
    private static readonly string[] ForbiddenWords = ["angular", "devextreme", "blazor", "mbh", "master.md", "dx-"];

    private static IEnumerable<string> SharedFiles(string root)
    {
        string[] folders = ["agents", "skills", "templates"];
        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }

        string[] files = ["onboarding/AGENTS.md", "onboarding/PROJECT.md", "CLAUDE.md", "CONTEXT.md"];
        foreach (var file in files)
        {
            yield return Path.Combine(root, file);
        }

        var sourceFolder = Path.Combine(root, "src");
        var separator = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*.cs", SearchOption.AllDirectories))
        {
            var isBuildOutput =
                file.Contains($"{separator}obj{separator}") || file.Contains($"{separator}bin{separator}");
            var isPackage = file.Contains($"{separator}node_modules{separator}");
            if (!isBuildOutput && !isPackage)
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// The text the rule checks. CLAUDE.md's "## Dashboard" section is left out: the dashboard is part of the runner
    /// itself, not a product repo, so it may name its own UI framework there.
    /// </summary>
    private static string CheckedText(string root, string file)
    {
        var text = File.ReadAllText(file);
        var isClaudeFile = Path.GetRelativePath(root, file) == "CLAUDE.md";
        if (!isClaudeFile)
        {
            return text;
        }

        var sectionStart = text.IndexOf("\n## Dashboard", StringComparison.Ordinal);
        if (sectionStart < 0)
        {
            return text;
        }

        var sectionEnd = text.IndexOf("\n## ", sectionStart + 1, StringComparison.Ordinal);

        return sectionEnd < 0 ? text[..sectionStart] : text[..sectionStart] + text[sectionEnd..];
    }

    [Fact]
    public void Shared_files_name_no_stack_framework_or_project()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();

        foreach (var file in SharedFiles(root))
        {
            var text = CheckedText(root, file);
            foreach (var word in ForbiddenWords.Where(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add($"{Path.GetRelativePath(root, file)}: {word}");
            }
        }

        Assert.True(offenders.Count == 0, "Stack or project names in shared files:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_dashboard_section_that_is_left_out_of_the_check_exists_in_claude_md()
    {
        // If the heading is renamed, CheckedText would silently stop leaving the section out (or the exemption
        // would hide the wrong text), so the rename must fail here first.
        var text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "CLAUDE.md"));

        Assert.Contains("\n## Dashboard", text);
    }

    [Fact]
    public void The_ui_checklist_skill_is_installed_with_the_other_skills()
    {
        var root = TestPaths.RepoRoot();
        var target = Directory.CreateTempSubdirectory("ai-runner-skills-").FullName;

        try
        {
            Workspace.CopyDirectory(Path.Combine(root, "skills"), target);

            var skillFile = Path.Combine(target, "ui-checklist", "SKILL.md");
            Assert.True(File.Exists(skillFile));
            Assert.Contains("name: ui-checklist", File.ReadAllText(skillFile));
        }
        finally
        {
            FileSystem.DeleteDirectory(target);
        }
    }

    [Fact]
    public void The_example_files_for_the_sample_project_are_marked_as_examples()
    {
        var root = TestPaths.RepoRoot();
        var examples = Directory.GetFiles(Path.Combine(root, "onboarding", "examples", "mbh"), "*.AGENTS.md");

        Assert.Equal(2, examples.Length);
        Assert.All(examples, file => Assert.Contains("EXAMPLE", File.ReadAllText(file)));
    }
}
