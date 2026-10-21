import { Component, computed, input } from '@angular/core';
import { SubIssueCountResponse } from '../../api/models/sub-issue-count-response';

/**
 * A thin bar with "closed/total" for a parent issue, from the API's `progress` count. It only turns the two numbers
 * the API sent into a width; it never counts sub-issues itself.
 */
@Component({
  selector: 'app-sub-issue-progress',
  template: `
    <span
      class="track"
      role="progressbar"
      aria-label="Sub-issues closed"
      aria-valuemin="0"
      [attr.aria-valuemax]="progress().total"
      [attr.aria-valuenow]="progress().closed"
      ><span class="fill" [style.width.%]="percent()"></span
    ></span>
    <span class="text">{{ progress().closed }}/{{ progress().total }}</span>
  `,
  styles: `
    :host {
      display: flex;
      align-items: center;
      gap: var(--app-space-2);
      min-width: 0;
    }

    .track {
      flex: 1 1 auto;
      min-width: 40px;
      max-width: 160px;
      height: 4px;
      overflow: hidden;
      border-radius: 999px;
      background: var(--mat-sys-outline-variant);
    }

    .fill {
      display: block;
      height: 100%;
      background: var(--app-tone-success-dot);
    }

    .text {
      flex: none;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class SubIssueProgress {
  readonly progress = input.required<SubIssueCountResponse>();

  protected readonly percent = computed(() => {
    const { closed, total } = this.progress();

    return total > 0 ? (closed * 100) / total : 0;
  });
}
