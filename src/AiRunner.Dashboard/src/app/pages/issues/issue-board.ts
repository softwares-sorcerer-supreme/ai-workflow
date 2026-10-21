import { DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { CountResponse } from '../../api/models/count-response';
import { IssueRowResponse } from '../../api/models/issue-row-response';
import { IssueCard } from '../../shared/issue-card/issue-card';
import { laneTone } from '../../shared/tone';

/** One board column: a lane as the API counts it, with the issues the API put in it, in the API's order. */
export interface IssueBoardColumn extends CountResponse {
  issues: IssueRowResponse[];
}

/**
 * The board of the issues screen: one column per lane (label, the API's count, a tone dot) with one card per issue.
 * It scrolls sideways inside its own box; at 1280 px and wider all five lanes fit.
 */
@Component({
  selector: 'app-issue-board',
  imports: [DecimalPipe, IssueCard],
  template: `
    @for (column of columns(); track column.code) {
      <section class="lane" [attr.data-lane]="column.code" [attr.aria-labelledby]="'lane-' + column.code">
        <h3 class="lane-head" [id]="'lane-' + column.code" [class]="'tone-' + laneTone(column.code)">
          <span class="dot" aria-hidden="true"></span>
          <span class="lane-label">{{ column.label }}</span>&ngsp;<span class="count">{{ column.count | number }}</span>
        </h3>
        <ul class="cards">
          @for (issue of column.issues; track issue.repo + '#' + issue.number) {
            <li><app-issue-card [issue]="issue" /></li>
          } @empty {
            <li class="nothing app-muted">Nothing here</li>
          }
        </ul>
      </section>
    }
  `,
  styleUrl: './issue-board.scss',
})
export class IssueBoard {
  readonly columns = input.required<readonly IssueBoardColumn[]>();

  protected readonly laneTone = laneTone;
}
