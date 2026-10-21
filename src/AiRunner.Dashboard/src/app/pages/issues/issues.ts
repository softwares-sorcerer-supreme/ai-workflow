import { Component, computed, effect, inject, linkedSignal, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, debounceTime } from 'rxjs';
import { Api } from '../../api/api';
import { getIssues } from '../../api/fn/issues/get-issues';
import { IssueRowResponse } from '../../api/models/issue-row-response';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { CountToggle } from '../../shared/count-toggle/count-toggle';
import { issueKey, issueLink } from '../../shared/issue-link';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { PullLink } from '../../shared/pull-link/pull-link';
import { StageChip } from '../../shared/stage-chip/stage-chip';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { SubIssueProgress } from '../../shared/sub-issue-progress/sub-issue-progress';
import { AppTime } from '../../shared/time/time';
import { focusTone, issueMarkIcon, issueMarkTone, laneTone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';
import {
  DEFAULT_ISSUE_FILTERS,
  ISSUE_SORT_OPTIONS,
  ISSUE_STATE_OPTIONS,
  IssueFilters,
  IssueView,
  hasIssueFilters,
  isIssueListNarrowed,
  issueQueryParams,
  issueRequest,
  readIssueFilters,
} from './issue-filters';
import { BoardLane, IssueBoard } from './issue-board';

/** How long the search waits after the last key before it asks the API. */
export const SEARCH_DEBOUNCE_MS = 300;

/** One line of the table: an issue, or a sub-issue shown indented right under its expanded parent. */
export interface IssueTableRow extends IssueRowResponse {
  key: string;
  isSubIssue: boolean;
  hasSubIssues: boolean;
  isExpanded: boolean;
}

/**
 * Where every issue the runner polls stands and which ones need a person. The filters live in the URL (so a reload
 * or a shared link shows the same screen) and are sent to the API, which matches, sorts, counts and decides lanes,
 * stages and actions. The list nests sub-issues under their parent; the board shows one column per lane.
 */
@Component({
  selector: 'app-issues',
  imports: [
    AppTime,
    CountToggle,
    DataCell,
    DataTable,
    InlineNote,
    IssueBoard,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatTooltipModule,
    PageHeader,
    PullLink,
    RouterLink,
    StageChip,
    StatusChip,
    SubIssueProgress,
    ViewState,
  ],
  templateUrl: './issues.html',
  styleUrl: './issues.scss',
})
export class Issues {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly stateOptions = ISSUE_STATE_OPTIONS;
  protected readonly sortOptions = ISSUE_SORT_OPTIONS;

  private readonly queryParams = toSignal(this.route.queryParamMap, { requireSync: true });

  protected readonly filters = computed(() => readIssueFilters(this.queryParams()));
  protected readonly isBoard = computed(() => this.filters().view === 'board');
  protected readonly hasFilters = computed(() => hasIssueFilters(this.filters()));

  /** Equal requests compare equal, so a URL change that does not change the request does not reload. */
  private readonly request = computed(() => issueRequest(this.filters()), {
    equal: (left, right) => JSON.stringify(left) === JSON.stringify(right),
  });

  protected readonly issues = pollResource(() => this.api.invoke(getIssues, this.request()));

  protected readonly projectOptions = computed(() => this.issues.value()?.projects ?? []);
  protected readonly stageOptions = computed(() => this.issues.value()?.stages ?? []);
  protected readonly lanes = computed(() => this.issues.value()?.lanes ?? []);
  protected readonly focuses = computed(() => this.issues.value()?.focuses ?? []);
  protected readonly total = computed(() => this.issues.value()?.total);

  /** The components of the chosen project, or of every project; a name shared by two projects is listed once. */
  protected readonly componentOptions = computed(() => {
    const chosen = this.filters().project;
    const projects = this.projectOptions().filter((option) => !chosen || option.name === chosen);
    const names = projects.flatMap((option) => option.components);

    return [...new Set(names)];
  });

  /**
   * What the search box shows. It follows the URL only when `q` changes from outside (back/forward, Clear filters),
   * never on the echo of what the box sent itself, so a trailing space or keys typed meanwhile are never overwritten.
   */
  protected readonly searchText = signal(this.filters().q);
  /** The last `q` the search box wrote into the URL. */
  private sentSearch = this.filters().q;
  private readonly searchInput = new Subject<string>();

  /**
   * Parents the user opened or closed by hand. Reset whenever the narrowing filters change, so a new lane, focus,
   * stage or search starts from its default (expanded when narrowed, collapsed otherwise).
   */
  private readonly expandedByHand = linkedSignal<string, ReadonlyMap<string, boolean>>({
    source: () => {
      const filters = this.filters();

      return [filters.lane, filters.focus, filters.stage, filters.q].join('\u0000');
    },
    computation: () => new Map(),
  });
  private readonly isExpandedByDefault = computed(() => isIssueListNarrowed(this.filters()));

  private readonly parentKeys = computed(() =>
    (this.issues.value()?.items ?? []).filter((item) => item.subIssues.length > 0).map(issueKey),
  );
  protected readonly hasParents = computed(() => this.parentKeys().length > 0);
  protected readonly isAllExpanded = computed(() => this.parentKeys().every((key) => this.isExpanded(key)));

  /** The nested answer as table lines: each parent, followed by its sub-issues when it is expanded. */
  protected readonly rows = computed<IssueTableRow[] | undefined>(() =>
    this.issues.value()?.items.flatMap((item) => {
      const key = issueKey(item);
      const hasSubIssues = item.subIssues.length > 0;
      const isExpanded = hasSubIssues && this.isExpanded(key);
      const parent: IssueTableRow = { ...item, key, isSubIssue: false, hasSubIssues, isExpanded };

      if (!isExpanded) {
        return [parent];
      }

      const subIssues = item.subIssues.map((subIssue) => ({
        ...subIssue,
        key: issueKey(subIssue),
        isSubIssue: true,
        hasSubIssues: false,
        isExpanded: false,
      }));

      return [parent, ...subIssues];
    }),
  );

  /** The lanes in the API's order, each with its issues; grouping by the lane code the API sent is layout only. */
  protected readonly boardLanes = computed<BoardLane[]>(() => {
    const answer = this.issues.value();

    if (!answer) {
      return [];
    }

    return answer.lanes.map((lane) => ({
      ...lane,
      issues: answer.items.filter((item) => item.lane === lane.code),
    }));
  });

  protected readonly columns: readonly DataColumn[] = [
    { key: 'issue', header: 'Issue' },
    { key: 'stage', header: 'Stage', width: '190px' },
    { key: 'next', header: 'Next', width: '220px' },
    { key: 'flags', header: 'Flags', hideBelow: 'sm' },
    { key: 'pulls', header: 'PR', width: '110px', hideBelow: 'sm' },
    { key: 'updatedAt', header: 'Updated', width: '110px', hideBelow: 'md' },
  ];

  protected readonly link = (row: IssueRowResponse) => issueLink(row.repo, row.number);
  protected readonly markTone = issueMarkTone;
  protected readonly markIcon = issueMarkIcon;
  protected readonly laneTone = laneTone;
  protected readonly focusTone = focusTone;
  protected readonly trackByIssue = (_index: number, row: IssueTableRow) => row.key;

  constructor() {
    // A new request (a filter set here, or the URL changed by back/forward) is loaded at once, not on the next poll.
    // The first run is the initial load, which pollResource has already started.
    let isFirstRun = true;
    effect(() => {
      this.request();

      if (isFirstRun) {
        isFirstRun = false;

        return;
      }

      untracked(() => this.issues.reload());
    });

    effect(() => {
      const query = this.filters().q;

      untracked(() => {
        if (query !== this.sentSearch) {
          this.sentSearch = query;
          this.searchText.set(query);
        }
      });
    });

    this.searchInput
      .pipe(debounceTime(SEARCH_DEBOUNCE_MS), takeUntilDestroyed())
      .subscribe((text) => this.sendSearch(text));
  }

  protected setProject(name: string): void {
    // A component of another project would match nothing (and the API refuses it).
    this.update({ project: name, component: '' });
  }

  protected setComponent(name: string): void {
    this.update({ component: name });
  }

  protected setState(code: string): void {
    this.update({ state: code });
  }

  protected setStage(code: string): void {
    this.update({ stage: code });
  }

  protected setSort(code: string): void {
    this.update({ sort: code });
  }

  protected setFlat(isFlat: boolean): void {
    this.update({ flat: isFlat });
  }

  protected setView(view: IssueView): void {
    this.update({ view });
  }

  protected setFocus(code: string): void {
    this.update({ focus: code });
  }

  /** A click on the selected lane clears it. */
  protected toggleLane(code: string): void {
    this.update({ lane: this.filters().lane === code ? '' : code });
  }

  protected onSearch(text: string): void {
    this.searchText.set(text);
    this.searchInput.next(text);
  }

  /** Clears at once; the empty text also replaces a search still waiting for its debounce. */
  protected clearSearch(): void {
    this.searchText.set('');
    this.searchInput.next('');
    this.sendSearch('');
  }

  /** Back to the default filters; the chosen view stays. */
  protected clearFilters(): void {
    this.searchText.set('');
    this.searchInput.next('');
    this.sentSearch = DEFAULT_ISSUE_FILTERS.q;
    this.update({ ...DEFAULT_ISSUE_FILTERS, view: this.filters().view });
  }

  private sendSearch(text: string): void {
    const query = text.trim();

    this.sentSearch = query;
    this.update({ q: query }, true);
  }

  protected toggle(row: IssueTableRow): void {
    const byHand = new Map(this.expandedByHand());
    byHand.set(row.key, !row.isExpanded);
    this.expandedByHand.set(byHand);
  }

  protected toggleAll(): void {
    const isExpanded = !this.isAllExpanded();
    const byHand = new Map(this.parentKeys().map((key) => [key, isExpanded] as const));
    this.expandedByHand.set(byHand);
  }

  private isExpanded(key: string): boolean {
    return this.expandedByHand().get(key) ?? this.isExpandedByDefault();
  }

  /** Writes a change into the URL; the request follows the URL. Typing replaces the history entry instead of adding. */
  private update(change: Partial<IssueFilters>, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: issueQueryParams(change),
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }
}
