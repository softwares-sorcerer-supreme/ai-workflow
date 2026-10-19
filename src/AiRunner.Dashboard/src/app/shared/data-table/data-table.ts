import { NgTemplateOutlet } from '@angular/common';
import {
  Component,
  Directive,
  TemplateRef,
  computed,
  contentChildren,
  inject,
  input,
  output,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTableModule } from '@angular/material/table';
import { ViewState } from '../view-state/view-state';

/** One column of a data table. */
export interface DataColumn {
  /** Matches an `appCell` template; without one the cell shows `row[key]` as text. */
  key: string;
  header: string;
  /** `end` for numbers and durations. */
  align?: 'start' | 'end';
  /** Hide the column on narrow screens: `sm` below 600 px, `md` below 900 px. */
  hideBelow?: 'sm' | 'md';
  /** Fixed CSS width, e.g. `120px`; otherwise the column takes what it needs. */
  width?: string;
}

/** The context of an `appCell` template: `let-row` is the row. */
export interface DataCellContext<T> {
  $implicit: T;
}

/** Custom content for one column: `<ng-template appCell="stage" let-row>...</ng-template>`. */
@Directive({ selector: 'ng-template[appCell]' })
export class DataCell<T = unknown> {
  readonly key = input.required<string>({ alias: 'appCell' });
  readonly template = inject<TemplateRef<DataCellContext<T>>>(TemplateRef);
}

/**
 * The one table of the dashboard: a Material table with typed columns, custom cells, and the loading, empty and
 * error states built in. Data stays on screen when a refresh fails (a small banner says so). It scrolls sideways
 * inside its own box on narrow screens; the page never does.
 */
@Component({
  selector: 'app-data-table',
  imports: [MatTableModule, MatIconModule, NgTemplateOutlet, ViewState],
  templateUrl: './data-table.html',
  styleUrl: './data-table.scss',
})
export class DataTable<T extends object> {
  readonly rows = input<readonly T[] | undefined>();
  readonly columns = input.required<readonly DataColumn[]>();
  /** True until the first answer: shows the loading state when there are no rows yet. */
  readonly loading = input(false);
  /** Text of the last failure: the error state without rows, a banner above existing rows. */
  readonly error = input<string>();
  readonly emptyTitle = input('Nothing to show');
  readonly emptyMessage = input<string>();
  /** Makes rows look and act clickable and emits `rowClick`. */
  readonly clickable = input(false);
  /** Stable identity of a row, so a refresh does not rebuild the table. Defaults to the row object. */
  readonly trackBy = input<(index: number, row: T) => unknown>((_index, row) => row);

  readonly rowClick = output<T>();
  readonly retry = output<void>();

  private readonly cells = contentChildren(DataCell);

  protected readonly columnKeys = computed(() => this.columns().map((column) => column.key));

  protected readonly templates = computed(() => {
    const byKey = new Map<string, TemplateRef<DataCellContext<T>>>();

    for (const cell of this.cells()) {
      byKey.set(cell.key(), cell.template as TemplateRef<DataCellContext<T>>);
    }

    return byKey;
  });

  protected readonly state = computed<'loading' | 'error' | 'empty' | 'rows'>(() => {
    const rows = this.rows();

    if (rows && rows.length > 0) {
      return 'rows';
    }

    if (this.loading() && !rows) {
      return 'loading';
    }

    if (this.error() && !rows) {
      return 'error';
    }

    return 'empty';
  });

  protected textOf(row: T, key: string): string {
    const value = (row as Record<string, unknown>)[key];

    return value === null || value === undefined ? '-' : String(value);
  }

  protected onRowClick(row: T): void {
    if (this.clickable()) {
      this.rowClick.emit(row);
    }
  }
}
