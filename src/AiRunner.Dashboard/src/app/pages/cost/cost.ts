import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Api } from '../../api/api';
import { getCost } from '../../api/fn/cost/get-cost';
import { CostRowResponse } from '../../api/models/cost-row-response';
import { DataCell, DataColumn, DataTable } from '../../shared/data-table/data-table';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { modelUsesText } from '../../shared/model-text';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { DurationPipe } from '../../shared/time/duration.pipe';
import { ViewState } from '../../shared/view-state/view-state';
import { CostChart, CostChartLayout, MAX_BARS } from './cost-chart';
import { COST_GROUPINGS, DEFAULT_COST_GROUPING } from './cost-filters';

/** A table line: a group from the API, or the API's total as the last line. */
interface CostLine extends CostRowResponse {
  isTotal: boolean;
  /** The row's models as one line ("claude-opus-5.5 (issue override) x2"); empty when the API sends none. */
  modelsText: string;
}

/**
 * What Copilot billed, its tokens, calls, jobs and job time per day, project, issue or model, for an optional range of
 * local days. The API groups, orders and totals; this screen only shows the rows, the total line and one bar chart of
 * the billed value.
 */
@Component({
  selector: 'app-cost',
  imports: [
    CostChart,
    DataCell,
    DataTable,
    DecimalPipe,
    DurationPipe,
    InlineNote,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatInputModule,
    PageHeader,
    ViewState,
  ],
  templateUrl: './cost.html',
  styleUrl: './cost.scss',
})
export class Cost {
  private readonly api = inject(Api);

  protected readonly groupings = COST_GROUPINGS;
  protected readonly maxBars = MAX_BARS;

  protected readonly by = signal(DEFAULT_COST_GROUPING);
  /** `yyyy-MM-dd` from the date inputs; empty means open-ended. */
  protected readonly from = signal('');
  protected readonly to = signal('');

  protected readonly cost = pollResource(() =>
    this.api.invoke(getCost, {
      by: this.by(),
      from: this.from() || undefined,
      to: this.to() || undefined,
    }),
  );

  /** Days run left to right over time; projects, issues and models are easier to read as labelled horizontal bars. */
  protected readonly layout = computed<CostChartLayout>(() =>
    this.cost.value()?.by === 'day' ? 'columns' : 'bars',
  );

  /** The API's rows plus its total as the last line; nothing is added up here. No rows: no lines (empty state). */
  protected readonly lines = computed<CostLine[] | undefined>(() => {
    const answer = this.cost.value();

    if (!answer) {
      return undefined;
    }

    if (answer.rows.length === 0) {
      return [];
    }

    const rows = answer.rows.map((row) => ({
      ...row,
      isTotal: false,
      modelsText: modelUsesText(row.models),
    }));

    // The key only keeps the total's track id apart from a group that happens to be called "total".
    const total = {
      ...answer.total,
      key: '\u0000total',
      isTotal: true,
      modelsText: modelUsesText(answer.total.models),
    };

    return [...rows, total];
  });

  protected readonly columns: readonly DataColumn[] = [
    { key: 'label', header: 'Group' },
    { key: 'calls', header: 'Copilot calls', align: 'end', width: '120px' },
    { key: 'jobs', header: 'Jobs', align: 'end', width: '90px' },
    { key: 'duration', header: 'Job time', align: 'end', width: '130px', hideBelow: 'sm' },
    { key: 'nanoAiu', header: 'Billed (nano AIU)', align: 'end', width: '160px' },
    { key: 'inputTokens', header: 'Input', align: 'end', width: '120px', hideBelow: 'md' },
    { key: 'cacheReadTokens', header: 'Cache read', align: 'end', width: '120px', hideBelow: 'md' },
    { key: 'cacheWriteTokens', header: 'Cache write', align: 'end', width: '120px', hideBelow: 'md' },
    { key: 'outputTokens', header: 'Output', align: 'end', width: '120px', hideBelow: 'md' },
  ];

  protected readonly trackByKey = (_index: number, row: CostLine) => row.key;

  protected setBy(code: string): void {
    this.by.set(code);
    this.cost.reload();
  }

  protected setFrom(value: string): void {
    this.from.set(value);
    this.cost.reload();
  }

  protected setTo(value: string): void {
    this.to.set(value);
    this.cost.reload();
  }
}
