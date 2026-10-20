import { FilterOption } from '../../shared/filter-option';

/**
 * The groupings of the cost screen. The API has no endpoint that lists them, so they live here, once. Codes must
 * match C# `CostReader.CodeOf`; an unknown code is answered with a 400.
 */
export const COST_GROUPINGS: readonly FilterOption[] = [
  { code: 'day', label: 'Day' },
  { code: 'project', label: 'Project' },
  { code: 'issue', label: 'Issue' },
];

/** The grouping the screen opens with. */
export const DEFAULT_COST_GROUPING = 'day';
