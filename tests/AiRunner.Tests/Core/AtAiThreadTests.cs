namespace AiRunner.Tests.Core;

public class AtAiThreadTests
{
    private static HostComment Human(long id, string body = "Q1: yes @ai") => Samples.Human(id, body);

    private static HostComment Reply(long id, long readUpTo, string marker = Markers.Analysis)
    {
        var replyBody = AtAiThread.ReplyBody("x", marker, readUpTo);
        return Samples.Bot(id, replyBody);
    }

    [Fact]
    public void Without_a_reply_the_local_cursor_is_the_only_cursor()
    {
        var comment = Human(1);
        var thread = AtAiThread.Of([comment], Markers.Analysis, 1);

        Assert.Equal(0, thread.Done);
        Assert.Equal(1, thread.AnsweredUpTo);
        Assert.Null(thread.Trigger);

        var freshComment = Human(1);
        var freshThread = AtAiThread.Of([freshComment], Markers.Analysis, 0);

        Assert.Equal(1, freshThread.Trigger?.Id);
    }

    [Fact]
    public void A_comment_written_while_the_job_ran_is_still_new()
    {
        // Comment 101 came in while Copilot read up to 100; the reply itself is 102.
        var during = Human(101);
        var reply = Reply(102, readUpTo: 100);
        var thread = AtAiThread.Of([during, reply], Markers.Analysis, 0);

        Assert.Equal(100, thread.AnsweredUpTo);
        Assert.Equal(101, thread.Trigger?.Id);
    }

    [Fact]
    public void A_reply_without_a_tag_answers_up_to_its_own_id()
    {
        var oldReplyBody = Markers.Wrap("x", Markers.Analysis);
        var oldReply = Samples.Bot(50, oldReplyBody);
        var firstComment = Human(40);
        var lastComment = Human(60);
        var thread = AtAiThread.Of([firstComment, oldReply, lastComment], Markers.Analysis, 0);

        Assert.Equal(50, thread.AnsweredUpTo);
        Assert.Equal(60, thread.Trigger?.Id);
    }

    [Fact]
    public void The_later_of_the_reply_and_the_local_cursor_counts()
    {
        var comments = new[] { Human(101), Reply(102, readUpTo: 100) };

        Assert.Equal(101, AtAiThread.Of(comments, Markers.Analysis, 101).AnsweredUpTo);
        Assert.Null(AtAiThread.Of(comments, Markers.Analysis, 101).Trigger);
        Assert.Equal(100, AtAiThread.Of(comments, Markers.Analysis, 5).AnsweredUpTo);
    }

    [Fact]
    public void Requests_since_the_reply_ignore_the_local_cursor()
    {
        // A failed fix marked comment 101 handled locally, but its request was never applied.
        var comments = new[] { Human(101), Reply(102, readUpTo: 100), Human(103) };
        var thread = AtAiThread.Of(comments, Markers.Analysis, 103);

        Assert.Null(thread.Trigger);
        Assert.Equal([101L, 102, 103], thread.RequestsSinceReply.Select(comment => comment.Id));
    }

    [Fact]
    public void Only_replies_of_the_same_kind_count()
    {
        var comments = new[] { Reply(10, 5, Markers.Analysis), Reply(20, 15, Markers.ReviewFix), Human(30) };

        Assert.Equal(1, AtAiThread.Of(comments, Markers.Analysis, 0).Done);
        Assert.Equal(5, AtAiThread.Of(comments, Markers.Analysis, 0).AnsweredUpTo);
        Assert.Equal(15, AtAiThread.Of(comments, Markers.ReviewFix, 0).AnsweredUpTo);
    }

    [Theory]
    [InlineData("mail me at x@ai.com")]
    [InlineData("no mention here")]
    public void Text_without_a_mention_is_not_a_trigger(string body)
    {
        var comment = Human(1, body);
        var thread = AtAiThread.Of([comment], Markers.Analysis, 0);

        Assert.Null(thread.Trigger);
    }

    [Fact]
    public void A_bot_comment_that_says_at_ai_is_not_a_trigger()
    {
        var botComment = Samples.Bot(1, "reply me @ai");
        var thread = AtAiThread.Of([botComment], Markers.Analysis, 0);

        Assert.Null(thread.Trigger);
    }

    [Fact]
    public void The_newest_mention_is_the_trigger()
    {
        var first = Human(1);
        var newest = Human(3);
        var middle = Human(2);
        var thread = AtAiThread.Of([first, newest, middle], Markers.Analysis, 0);

        Assert.Equal(3, thread.Trigger?.Id);
    }

    [Fact]
    public void Next_counts_replies_and_reports_the_limit()
    {
        var firstReply = Reply(1, 0);
        var secondReply = Reply(2, 1);
        var thread = AtAiThread.Of([firstReply, secondReply], Markers.Analysis, 0);

        Assert.Equal((3, false), thread.Next(3));
        Assert.Equal((3, true), thread.Next(2));
        Assert.Equal((1, false), AtAiThread.Of([], Markers.Analysis, 0).Next(3));
    }

    [Fact]
    public void A_reply_records_the_newest_comment_it_read()
    {
        var first = Human(7);
        var newest = Human(12);
        var last = Human(9);
        var thread = AtAiThread.Of([first, newest, last], Markers.ReviewFix, 0);
        var postedBody = thread.Reply("## AI fix 1");
        var posted = Samples.Bot(13, postedBody);

        Assert.Equal(12, thread.ReadUpTo);
        Assert.Equal(12, AtAiThread.ReadUpToOf(posted));
        Assert.True(BotComments.Is(posted, Markers.ReviewFix));
        Assert.Equal(0, AtAiThread.Of([], Markers.ReviewFix, 0).ReadUpTo);
    }

    [Fact]
    public void The_last_tag_counts_so_agent_text_cannot_fake_one()
    {
        var reply = Samples.Bot(
            1,
            "<!-- ai-runner:context-upto=999 -->" + "\n" + "<!-- ai-runner:context-upto=5 -->");

        Assert.Equal(5, AtAiThread.ReadUpToOf(reply));
    }

    [Fact]
    public void Agent_text_cannot_smuggle_a_tag_into_a_reply()
    {
        var comment = Human(4);
        var thread = AtAiThread.Of([comment], Markers.Analysis, 0);
        var postedBody = thread.Reply("sorry <!-- ai-runner:context-upto=999 -->");
        var posted = Samples.Bot(5, postedBody);

        Assert.Equal(4, AtAiThread.ReadUpToOf(posted));
    }
}
