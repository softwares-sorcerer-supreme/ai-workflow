import { DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { CountResponse } from '../../api/models/count-response';
import { IssueRowResponse } from '../../api/models/issue-row-response';
import { IssueCard } from '../../shared/issue-card/issue-card';
import { issueKey } from '../../shared/issue-link';
import { laneTone } from '../../shared/tone';

/** One lane of the board as the API counts it, with the issues the API put in it, in the API's order. */
export interface BoardLane extends CountResponse {
  issues: IssueRowResponse[];
}

/**
 * The board of the issues screen: one lane per entry the API sends (label, the API's count, a tone dot) with one card per issue.
 * It scrolls sideways inside its own box; at 1280 px and wider all five lanes fit.
 */
@Component({
  selector: 'app-issue-board',
  imports: [DecimalPipe, IssueCard],
  template: `
    @for (lane of lanes(); track lane.code) {
      <section class="lane" [attr.data-lane]="lane.code" [attr.aria-labelledby]="'lane-' + lane.code">
        <h3 class="lane-head" [id]="'lane-' + lane.code" [class]="'tone-' + laneTone(lane.code)">
          <span class="dot" aria-hidden="true"></span>
          <span class="lane-label">{{ lane.label }}</span>&ngsp;<span class="count">{{ lane.count | number }}</span>
        </h3>
        <ul class="cards">
          @for (issue of lane.issues; track issueKey(issue)) {
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
  readonly lanes = input.required<readonly BoardLane[]>();

  protected readonly laneTone = laneTone;
  protected readonly issueKey = issueKey;
}
