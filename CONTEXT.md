# ai-runner

A runner on a developer laptop that watches issues and pull requests on GitBucket and answers them with analyses, sub-issues, code and reviews written by an AI agent.

## Language

**Bot comment**:
A comment the runner posted itself, found again by its hidden marker.
_Avoid_: AI comment, runner comment

**Block**:
A marked section inside a bot comment, one per kind (analysis, spec, review, review fix).
_Avoid_: section, message

**Thread**:
The bot comments of one block kind on one issue or pull request, together with the human comments around them.
_Avoid_: conversation, discussion

**Trigger**:
The newest human comment that mentions `@ai` and that no reply has answered yet.
_Avoid_: request, ping

**Reply**:
A bot comment that answers a trigger: an analysis (one **Round**) or a **Review fix**.
_Avoid_: response, answer

**Read-up-to**:
The newest comment id a job had read when it posted its reply; the reply records it, so comments written while the agent ran still count as new.
_Avoid_: context-upto (the stored tag), last read

**Answered-up-to**:
The point after which comments are new: the later of the last reply's read-up-to and the local cursor.
_Avoid_: handled-up-to, last handled

**Local cursor**:
The newest `@ai` comment the runner's own database says was handled; it covers a job that failed before it could reply.
_Avoid_: last-handled comment

**Round**:
One analysis reply on an issue, numbered and capped by a limit.
_Avoid_: iteration

**Attachment**:
A file a person linked from issue, comment or PR text through GitBucket's `_attached` route. The runner downloads it into the clone for the agent; it never uploads anything.
_Avoid_: upload, resource

**Review fix**:
One reply on a pull request after the agent changed the PR's branch to answer an `@ai` comment, numbered and capped by a limit.
_Avoid_: AI fix, patch round
