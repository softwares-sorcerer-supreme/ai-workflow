namespace AiRunner.Tests.Fakes;

/// <summary>Projects for tests that need a config but no job kit: the three layouts of the spec, by hand.</summary>
public static class TestProjects
{
    public const string Repo = "o/r";

    /// <summary>One component in o/r that also holds the issues, like a repo from the old flat list.</summary>
    public static ProjectConfig Single(
        string[]? workflows = null,
        string? reviewScope = null,
        int? maxReviewFixes = null,
        CommandsConfig? commands = null)
    {
        var component = new ComponentConfig { Name = "r", Repo = Repo, Commands = commands ?? new CommandsConfig() };
        var project = new ProjectConfig { Name = "r", IssuesIn = "r", Components = [component] };

        if (workflows is not null)
        {
            project.Settings.Workflows = [.. workflows];
        }

        if (reviewScope is not null)
        {
            project.Settings.Review = new ReviewConfig { Scope = reviewScope };
        }

        if (maxReviewFixes is not null)
        {
            project.Settings.Limits = new LimitsConfig { MaxReviewFixes = maxReviewFixes.Value };
        }

        return project;
    }

    /// <summary>One component per repo; the issues go to the backend.</summary>
    public static ProjectConfig MultiRepo() =>
        new()
        {
            Name = "duo",
            IssuesIn = "backend",
            Components =
            [
                new ComponentConfig { Name = "backend", Repo = "org/duo-backend" },
                new ComponentConfig { Name = "frontend", Repo = "org/duo-frontend" },
            ],
        };

    /// <summary>Backend and frontend repos plus an issues repo that holds no code.</summary>
    public static ProjectConfig SeparateIssuesRepo() =>
        new()
        {
            Name = "mbh",
            IssuesIn = "issues",
            Components =
            [
                new ComponentConfig { Name = "issues", Repo = "org/mbh-issues", Code = false },
                new ComponentConfig { Name = "backend", Repo = "org/mbh-backend" },
                new ComponentConfig { Name = "frontend", Repo = "org/mbh-frontend" },
            ],
        };

    /// <summary>Two apps in folders of one repo, plus the issues in the same repo.</summary>
    public static ProjectConfig Monorepo() =>
        new()
        {
            Name = "mono",
            IssuesIn = "web",
            Components =
            [
                new ComponentConfig { Name = "web", Repo = "org/mono", Path = "apps/web" },
                new ComponentConfig { Name = "api", Repo = "org/mono", Path = "apps/api" },
            ],
        };
}
