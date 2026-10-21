import { Component, computed, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { IssueRowResponse } from '../../api/models/issue-row-response';
import { ActionButtons, ActionTarget } from '../action-buttons/action-buttons';
import { issueKey, issueLink } from '../issue-link';
import { PullLink } from '../pull-link/pull-link';
import { StageChip } from '../stage-chip/stage-chip';
import { StatusChip } from '../status-chip/status-chip';
import { SubIssueProgress } from '../sub-issue-progress/sub-issue-progress';
import { AppTime } from '../time/time';
import { issueMarkIcon, issueMarkTone } from '../tone';

/**
 * One issue as a compact card (a lane of the board, a short list): number, where it lives, title, its parent, stage,
 * badge and flags, what a person must do, its PRs, sub-issue progress and when it last changed. Everything shown
 * comes ready from the API's issue row.
 */
@Component({
  selector: 'app-issue-card',
  imports: [ActionButtons, AppTime, MatIconModule, PullLink, RouterLink, StageChip, StatusChip, SubIssueProgress],
  templateUrl: './issue-card.html',
  styleUrl: './issue-card.scss',
})
export class IssueCard {
  readonly issue = input.required<IssueRowResponse>();
  /** After one of its actions was answered: the screen reloads (the card never changes itself). */
  readonly after = output<void>();

  protected readonly actionTarget = computed<ActionTarget>(() => ({
    kind: 'issue',
    repo: this.issue().repo,
    number: this.issue().number,
  }));

  protected readonly link = computed(() => issueLink(this.issue().repo, this.issue().number));

  /** Sub-issues live in their parent's repo (the project's issues component). */
  protected readonly parentLink = computed(() => {
    const issue = this.issue();

    return issue.parent === null ? null : issueLink(issue.repo, issue.parent);
  });

  protected readonly markTone = issueMarkTone;
  protected readonly markIcon = issueMarkIcon;
  protected readonly issueKey = issueKey;
}
