import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { DataCell, DataColumn, DataTable } from './data-table';

interface Row {
  name: string;
  count: number | null;
}

@Component({
  imports: [DataTable, DataCell],
  template: `
    <app-data-table
      [rows]="rows()"
      [columns]="columns"
      [loading]="loading()"
      [error]="error()"
      [clickable]="true"
      emptyTitle="No rows"
      (rowClick)="clicked.push($event)"
    >
      <ng-template appCell="name" let-row><b class="name">{{ row.name }}</b></ng-template>
    </app-data-table>
  `,
})
class Host {
  readonly rows = signal<Row[] | undefined>(undefined);
  readonly loading = signal(false);
  readonly error = signal<string | undefined>(undefined);
  readonly clicked: Row[] = [];
  readonly columns: DataColumn[] = [
    { key: 'name', header: 'Name' },
    { key: 'count', header: 'Count', align: 'end', hideBelow: 'sm' },
  ];
}

describe('DataTable', () => {
  async function render(setup: (host: Host) => void) {
    const fixture = TestBed.createComponent(Host);
    setup(fixture.componentInstance);
    await fixture.whenStable();

    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('renders custom cells and falls back to the plain value, "-" for null', async () => {
    const { element } = await render((host) =>
      host.rows.set([
        { name: 'shop', count: 3 },
        { name: 'crm', count: null },
      ]),
    );

    const rows = element.querySelectorAll('tr.mat-mdc-row');
    expect(rows.length).toBe(2);
    expect(rows[0].querySelector('.name')?.textContent).toBe('shop');
    expect(rows[0].querySelectorAll('td')[1].textContent?.trim()).toBe('3');
    expect(rows[1].querySelectorAll('td')[1].textContent?.trim()).toBe('-');
    expect(rows[0].querySelectorAll('td')[1].classList).toContain('align-end');
    expect(rows[0].querySelectorAll('td')[1].classList).toContain('hide-sm');
  });

  it('shows the loading state before the first answer', async () => {
    const { element } = await render((host) => host.loading.set(true));

    expect(element.querySelector('app-view-state.kind-loading')).not.toBeNull();
    expect(element.querySelector('table')).toBeNull();
  });

  it('shows the empty state for an empty list', async () => {
    const { element } = await render((host) => host.rows.set([]));

    expect(element.querySelector('app-view-state.kind-empty')?.textContent).toContain('No rows');
  });

  it('shows the error state when the first call failed', async () => {
    const { element } = await render((host) => host.error.set('The runner is not reachable.'));

    const state = element.querySelector('app-view-state.kind-error');
    expect(state?.textContent).toContain('The runner is not reachable.');
    expect(state?.textContent).toContain('Try again');
  });

  it('keeps the rows and shows a banner when a refresh failed', async () => {
    const { element } = await render((host) => {
      host.rows.set([{ name: 'shop', count: 1 }]);
      host.error.set('The runner is not reachable.');
    });

    expect(element.querySelectorAll('tr.mat-mdc-row').length).toBe(1);
    expect(element.querySelector('.stale')?.textContent).toContain('Showing older data');
  });

  it('emits the clicked row', async () => {
    const { fixture, element } = await render((host) => host.rows.set([{ name: 'shop', count: 1 }]));

    (element.querySelector('tr.mat-mdc-row') as HTMLElement).click();

    expect(fixture.componentInstance.clicked).toEqual([{ name: 'shop', count: 1 }]);
  });
});
