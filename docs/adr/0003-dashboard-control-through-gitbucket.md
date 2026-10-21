---
status: superseded by 0004
---

# Future dashboard actions go through GitBucket; settings stay in repos.yml

v1 of the dashboard is read-only. When control features come, an action (start an analysis, approve a breakdown or an implement, retry a failed job) will be a GitBucket write, such as adding a label or posting an `@ai` comment, that the planner then picks up on the next poll. The dashboard will not put jobs straight into the queue. This keeps `JobPlanner` the only authority on what runs, keeps the database deletable, and makes every action visible on the issue. Settings stay in `repos.yml` (strict and reviewable in git); a settings screen would edit that file rather than move settings into a database. Both points get their own design session before anything is built.
