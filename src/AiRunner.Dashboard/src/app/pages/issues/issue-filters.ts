import { ParamMap, Params } from '@angular/router';
import { GetIssues$Params } from '../../api/fn/issues/get-issues';
import { FilterOption } from '../../shared/filter-option';

/** List: the table with nested sub-issues and the pipeline summary. Board: one column per lane. */
export type IssueView = 'list' | 'board';

/** "All" in the State select: `state=all` in the URL and in the request (the API keeps open and closed issues). */
export const ALL_STATES = 'all';

/**
 * The state filter of the issues screen (open or closed on GitBucket, not the stage). Projects, components, stages,
 * lanes and focuses come with the API's answer; `open` and `closed` are fixed by C# `SnapshotRules.Open` / `Closed`.
 */
export const ISSUE_STATE_OPTIONS: readonly FilterOption[] = [
  { code: 'open', label: 'Open' },
  { code: 'closed', label: 'Closed' },
  { code: ALL_STATES, label: 'All' },
];

/** The sort codes of `GET /api/issues` (C# `IssueReader.SortByNumber` / `SortByUpdate`). */
export const ISSUE_SORT_OPTIONS: readonly FilterOption[] = [
  { code: 'number', label: 'Newest' },
  { code: 'updated', label: 'Recently updated' },
];

/** The filters of the issues screen as they stand in the URL (`/issues?state=closed&q=login&view=board`). */
export interface IssueFilters {
  project: string;
  component: string;
  state: string;
  stage: string;
  lane: string;
  focus: string;
  q: string;
  sort: string;
  flat: boolean;
  view: IssueView;
}

/** What a missing query parameter means. Empty text = no filter. */
export const DEFAULT_ISSUE_FILTERS: Readonly<IssueFilters> = {
  project: '',
  component: '',
  state: 'open',
  stage: '',
  lane: '',
  focus: '',
  q: '',
  sort: 'number',
  flat: false,
  view: 'list',
};

/** Reads the filters from the route's query parameters; a missing one takes its default. */
export function readIssueFilters(params: ParamMap): IssueFilters {
  const text = (key: keyof IssueFilters) => params.get(key) ?? (DEFAULT_ISSUE_FILTERS[key] as string);

  return {
    project: text('project'),
    component: text('component'),
    state: text('state'),
    stage: text('stage'),
    lane: text('lane'),
    focus: text('focus'),
    q: text('q'),
    sort: text('sort'),
    flat: params.get('flat') === 'true',
    view: params.get('view') === 'board' ? 'board' : 'list',
  };
}

/**
 * The query parameters for a change of filters, to merge into the URL. A value equal to its default (or empty) is
 * removed, so the URL holds only what differs and a plain `/issues` is the default screen.
 */
export function issueQueryParams(change: Partial<IssueFilters>): Params {
  const params: Params = {};

  for (const [key, value] of Object.entries(change)) {
    const fallback = DEFAULT_ISSUE_FILTERS[key as keyof IssueFilters];
    params[key] = value === fallback || value === '' ? null : String(value);
  }

  return params;
}

/**
 * The request for the filters. The board always asks for the flat list (every issue and sub-issue in its own lane)
 * and leaves out the stage and lane filters: its columns are the lanes.
 */
export function issueRequest(filters: IssueFilters): GetIssues$Params {
  const isBoard = filters.view === 'board';

  return {
    project: filters.project || undefined,
    component: filters.component || undefined,
    stage: isBoard ? undefined : filters.stage || undefined,
    state: filters.state || undefined,
    flat: isBoard || filters.flat || undefined,
    lane: isBoard ? undefined : filters.lane || undefined,
    focus: filters.focus || undefined,
    q: filters.q || undefined,
    sort: filters.sort === DEFAULT_ISSUE_FILTERS.sort ? undefined : filters.sort || undefined,
  };
}

/** True when a filter the current view uses differs from its default: "Clear filters" is then offered. */
export function hasIssueFilters(filters: IssueFilters): boolean {
  const defaults = DEFAULT_ISSUE_FILTERS;
  const isListFiltered = filters.view === 'list' && (!!filters.stage || !!filters.lane || filters.flat);

  return (
    !!filters.project ||
    !!filters.component ||
    filters.state !== defaults.state ||
    !!filters.focus ||
    !!filters.q ||
    filters.sort !== defaults.sort ||
    isListFiltered
  );
}

/**
 * True when the list is narrowed to some issues (lane, focus, stage or search): the matching sub-issues are then the
 * point, so parents start expanded.
 */
export function isIssueListNarrowed(filters: IssueFilters): boolean {
  return !!(filters.lane || filters.focus || filters.stage || filters.q);
}
