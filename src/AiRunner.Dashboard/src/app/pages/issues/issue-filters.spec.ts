import { convertToParamMap } from '@angular/router';
import {
  DEFAULT_ISSUE_FILTERS,
  hasIssueFilters,
  issueQueryParams,
  issueRequest,
  readIssueFilters,
} from './issue-filters';

describe('issue filters', () => {
  it('reads the defaults from an empty URL', () => {
    expect(readIssueFilters(convertToParamMap({}))).toEqual(DEFAULT_ISSUE_FILTERS);
  });

  it('removes values equal to their default from the URL', () => {
    expect(issueQueryParams({ state: 'open', sort: 'number', flat: false, view: 'list', q: '' })).toEqual({
      state: null,
      sort: null,
      flat: null,
      view: null,
      q: null,
    });
    expect(issueQueryParams({ state: 'all', flat: true, view: 'board' })).toEqual({
      state: 'all',
      flat: 'true',
      view: 'board',
    });
  });

  it('sends state=all for All and the flat list without stage or lane on the board', () => {
    const filters = { ...DEFAULT_ISSUE_FILTERS, state: 'all', stage: 'pr-open', lane: 'ready', view: 'board' as const };

    expect(issueRequest(filters)).toMatchObject({ state: 'all', stage: undefined, lane: undefined, flat: true });
  });

  it('counts stage, lane and flat as filters in the list only', () => {
    const listFilters = { ...DEFAULT_ISSUE_FILTERS, lane: 'ready' };

    expect(hasIssueFilters(DEFAULT_ISSUE_FILTERS)).toBe(false);
    expect(hasIssueFilters(listFilters)).toBe(true);
    expect(hasIssueFilters({ ...listFilters, view: 'board' })).toBe(false);
    expect(hasIssueFilters({ ...DEFAULT_ISSUE_FILTERS, state: 'all', view: 'board' })).toBe(true);
  });
});
