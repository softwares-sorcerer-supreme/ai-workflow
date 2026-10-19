import { FilterOption } from '../jobs/job-filters';

/**
 * The state filter of the issues screen (open or closed on GitBucket, not the stage). Projects, components and stages
 * come with the API's answer; these two codes are fixed by C# `SnapshotRules.Open` / `Closed`.
 */
export const ISSUE_STATE_OPTIONS: readonly FilterOption[] = [
  { code: 'open', label: 'Open' },
  { code: 'closed', label: 'Closed' },
];
