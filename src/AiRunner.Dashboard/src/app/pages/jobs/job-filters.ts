import { FilterOption } from '../../shared/filter-option';

/**
 * The status filter of the jobs screen. The API has no endpoint that lists these, so they live here, once. Codes
 * and labels must match C# `JobStatus` / `JobLabels.StatusLabel`; an unknown code is answered with a 400.
 */
export const JOB_STATUS_OPTIONS: readonly FilterOption[] = [
  { code: 'pending', label: 'Queued' },
  { code: 'running', label: 'Running' },
  { code: 'done', label: 'Done' },
  { code: 'failed', label: 'Failed' },
];

/** The type filter of the jobs screen. Codes and labels must match C# `JobType` / `JobLabels.TypeLabel`. */
export const JOB_TYPE_OPTIONS: readonly FilterOption[] = [
  { code: 'AnalyzeIssue', label: 'Analyze issue' },
  { code: 'BreakdownIssue', label: 'Break down issue' },
  { code: 'ImplementIssue', label: 'Implement issue' },
  { code: 'ReviewPullRequest', label: 'Review PR' },
  { code: 'FixReview', label: 'Fix review' },
  { code: 'SequenceSubIssues', label: 'Sequence sub-issues' },
  { code: 'FinishIssue', label: 'Finish issue' },
];
