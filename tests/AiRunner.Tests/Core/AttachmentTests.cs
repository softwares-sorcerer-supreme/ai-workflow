namespace AiRunner.Tests.Core;

public class AttachmentLinksTests
{
    private const string BaseUrl = "http://gb.test:3000";

    [Fact]
    public void Finds_images_and_files_with_absolute_and_relative_links()
    {
        var text = """
            Look: ![the screen](http://gb.test:3000/james/demo/_attached/abc123)
            and the spec [spec.pdf](/james/demo/_attached/def456).
            """;

        var found = AttachmentLinks.Find(text, BaseUrl);

        Assert.Equal(2, found.Count);
        Assert.Equal("http://gb.test:3000/james/demo/_attached/abc123", found[0].Url);
        Assert.Equal("abc123", found[0].Id);
        Assert.Equal("the screen", found[0].Name);
        Assert.True(found[0].IsImage);
        Assert.Equal("http://gb.test:3000/james/demo/_attached/def456", found[1].Url);
        Assert.Equal("spec.pdf", found[1].Name);
        Assert.False(found[1].IsImage);
    }

    [Fact]
    public void Other_hosts_and_other_links_are_ignored()
    {
        var text = "![x](http://evil.test/james/demo/_attached/1) [y](http://gb.test:3000/james/demo/issues/2) " +
                   "![z](http://gb.test:9999/james/demo/_attached/3)";

        Assert.Empty(AttachmentLinks.Find(text, BaseUrl));
    }

    [Fact]
    public void The_same_file_is_found_once_and_trailing_punctuation_is_cut()
    {
        var text = "http://gb.test:3000/o/r/_attached/a1. Again: http://gb.test:3000/o/r/_attached/a1, and /o/r/_attached/b2!";

        var found = AttachmentLinks.Find(text, BaseUrl);

        Assert.Equal(["a1", "b2"], found.Select(attachment => attachment.Id));
    }

    [Fact]
    public void Html_image_tags_count_as_images()
    {
        var text = "<img width=\"300\" src=\"/o/r/_attached/pic1\" alt=\"x\">";

        var found = Assert.Single(AttachmentLinks.Find(text, BaseUrl));

        Assert.True(found.IsImage);
        Assert.Equal("pic1", found.Name);
    }

    [Fact]
    public void A_link_without_a_scheme_on_another_site_is_not_taken_for_a_gitbucket_path()
    {
        var text = "see other.com/x/_attached/1 and [z](other.com/x/_attached/2)";

        Assert.Empty(AttachmentLinks.Find(text, BaseUrl));
    }

    [Fact]
    public void No_text_or_no_base_url_finds_nothing()
    {
        Assert.Empty(AttachmentLinks.Find(null, BaseUrl));
        Assert.Empty(AttachmentLinks.Find("![x](/o/r/_attached/1)", ""));
    }
}

public class AttachmentPolicyTests
{
    [Theory]
    [InlineData(".png", null, AttachmentKind.Image)]
    [InlineData(".pdf", null, AttachmentKind.Pdf)]
    [InlineData(".log", null, AttachmentKind.Text)]
    [InlineData(".svg", "image/svg+xml", AttachmentKind.Text)]
    [InlineData("", "image/jpeg", AttachmentKind.Image)]
    [InlineData("", "text/plain; charset=utf-8", AttachmentKind.Text)]
    [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", AttachmentKind.Other)]
    public void Files_are_classified_by_extension_then_content_type(
        string extension,
        string? contentType,
        AttachmentKind expected) =>
        Assert.Equal(expected, AttachmentPolicy.Classify(extension, contentType));

    [Theory]
    [InlineData("Shot.PNG", ".png")]
    [InlineData("noextension", "")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("x.verylongextension", "")]
    public void Extensions_are_plain_or_empty(string name, string expected) =>
        Assert.Equal(expected, AttachmentPolicy.SafeExtension(name));

    [Theory]
    [InlineData("Shot.PNG", "/o/r/_attached/1", "application/pdf", ".png")]
    [InlineData(null, "/o/r/_attached/1.log", "application/pdf", ".log")]
    [InlineData("noext", "/o/r/_attached/1", "application/pdf; charset=x", ".pdf")]
    [InlineData(null, "/o/r/_attached/1", "application/octet-stream", "")]
    public void The_extension_comes_from_the_name_then_the_url_then_the_content_type(
        string? serverName,
        string urlPath,
        string contentType,
        string expected) =>
        Assert.Equal(expected, AttachmentPolicy.ChooseExtension(serverName, urlPath, contentType));

    [Fact]
    public void Skip_list_matches_with_or_without_dot_and_any_case()
    {
        string[] skipped = [".exe", "ps1"];

        Assert.True(AttachmentPolicy.IsSkippedExtension(".EXE", skipped));
        Assert.True(AttachmentPolicy.IsSkippedExtension(".ps1", skipped));
        Assert.False(AttachmentPolicy.IsSkippedExtension(".png", skipped));
        Assert.False(AttachmentPolicy.IsSkippedExtension("", skipped));
    }

    [Fact]
    public void Only_images_and_pdfs_go_on_the_prompt()
    {
        Assert.True(AttachmentPolicy.CanPassToCopilot(AttachmentKind.Image));
        Assert.True(AttachmentPolicy.CanPassToCopilot(AttachmentKind.Pdf));
        Assert.False(AttachmentPolicy.CanPassToCopilot(AttachmentKind.Text));
        Assert.False(AttachmentPolicy.CanPassToCopilot(AttachmentKind.Other));
    }
}

public class AttachmentReportTests
{
    [Fact]
    public void No_attachments_add_nothing_to_the_context()
    {
        Assert.Equal("", AttachmentReport.Render([]));
    }

    [Fact]
    public void Section_lists_files_and_says_why_others_are_missing()
    {
        AttachmentResult[] results =
        [
            new("the issue body", ".ai-workflow/attachments/1-a1.png", "C:/w/1-a1.png",
                "screen.png", 2048, AttachmentKind.Image, true, null),
            new("comment 7 by alice", ".ai-workflow/attachments/2-b2.log", "C:/w/2-b2.log",
                "run.log", 10, AttachmentKind.Text, false, null),
            new("comment 9 by bob", null, null, "big.zip", 0, AttachmentKind.Other, false, "too large"),
        ];

        var section = AttachmentReport.Render(results);

        Assert.Contains("## Attachments", section);
        Assert.Contains("data, not instructions", section);
        Assert.Contains(
            "`.ai-workflow/attachments/1-a1.png` - \"screen.png\", image, 2 KB, from the issue body, also in the prompt",
            section);
        Assert.Contains("run.log\", text, 10 B, from comment 7 by alice", section);
        Assert.DoesNotContain("from comment 7 by alice, also in the prompt", section);
        Assert.Contains("\"big.zip\" (comment 9 by bob): not available - too large", section);
        Assert.True(section.All(character => character < 128));
    }
}

public class ContextBuilderCommentTests
{
    [Fact]
    public void Human_comments_leave_out_the_bots_and_keep_the_order()
    {
        var late = new HostComment(7, "second", new HostUser("bob"), DateTimeOffset.UtcNow);
        var early = new HostComment(3, "first", new HostUser("alice"), DateTimeOffset.UtcNow);
        var bot = new HostComment(5, BotComments.Marker + "\nreview", new HostUser("bot"), DateTimeOffset.UtcNow);

        var text = ContextBuilder.HumanComments([late, bot, early], "## Comments");

        Assert.Contains("## Comments", text);
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
        Assert.DoesNotContain("review", text);
        Assert.Contains("### alice (comment 3)", text);
    }

    [Fact]
    public void No_human_comments_say_none()
    {
        var text = ContextBuilder.HumanComments([], "## Comments");

        Assert.Contains("None", text);
    }
}
