using AiRunner.Infrastructure.CodeHost;

namespace AiRunner.Tests.Infrastructure;

public class GitBucketAttachmentLinksTests
{
    private const string BaseUrl = "http://gb.test:3000";

    [Fact]
    public void Finds_images_and_files_with_absolute_and_relative_links()
    {
        var text = """
            Look: ![the screen](http://gb.test:3000/james/demo/_attached/abc123)
            and the spec [spec.pdf](/james/demo/_attached/def456).
            """;

        var found = GitBucketAttachmentLinks.Find(text, BaseUrl);

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

        Assert.Empty(GitBucketAttachmentLinks.Find(text, BaseUrl));
    }

    [Fact]
    public void The_same_file_is_found_once_and_trailing_punctuation_is_cut()
    {
        var text = "http://gb.test:3000/o/r/_attached/a1. Again: http://gb.test:3000/o/r/_attached/a1, "
            + "and /o/r/_attached/b2!";

        var found = GitBucketAttachmentLinks.Find(text, BaseUrl);

        Assert.Equal(["a1", "b2"], found.Select(attachment => attachment.Id));
    }

    [Fact]
    public void Html_image_tags_count_as_images()
    {
        var text = "<img width=\"300\" src=\"/o/r/_attached/pic1\" alt=\"x\">";

        var found = Assert.Single(GitBucketAttachmentLinks.Find(text, BaseUrl));

        Assert.True(found.IsImage);
        Assert.Equal("pic1", found.Name);
    }

    [Fact]
    public void A_link_without_a_scheme_on_another_site_is_not_taken_for_a_gitbucket_path()
    {
        var text = "see other.com/x/_attached/1 and [z](other.com/x/_attached/2)";

        Assert.Empty(GitBucketAttachmentLinks.Find(text, BaseUrl));
    }

    [Fact]
    public void No_text_or_no_base_url_finds_nothing()
    {
        Assert.Empty(GitBucketAttachmentLinks.Find(null, BaseUrl));
        Assert.Empty(GitBucketAttachmentLinks.Find("![x](/o/r/_attached/1)", ""));
    }
}
