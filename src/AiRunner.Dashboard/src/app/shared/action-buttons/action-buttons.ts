import { Component, booleanAttribute, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Api } from '../../api/api';
import { runIssueAction } from '../../api/fn/issues/run-issue-action';
import { runPullAction } from '../../api/fn/pulls/run-pull-action';
import { ActionRequest } from '../../api/models/action-request';
import { ActionResponse } from '../../api/models/action-response';
import { CommandResponse } from '../../api/models/command-response';
import { splitRepo } from '../issue-link';
import { writeAction } from '../write-action';
import { ActionModelDialog, ActionModelDialogData } from './action-model-dialog';
import { ActionTextDialog, ActionTextDialogData } from './action-text-dialog';

/** What the actions act on: an issue or a PR, by its `owner/repo` (as the API sends it) and number. */
export interface ActionTarget {
  kind: 'issue' | 'pull';
  repo: string;
  number: number;
}

/** The target of the actions of an issue row. */
export function issueTarget(row: { repo: string; number: number }): ActionTarget {
  return { kind: 'issue', repo: row.repo, number: row.number };
}

/** The target of the actions of a PR row. */
export function pullTarget(row: { repo: string; number: number }): ActionTarget {
  return { kind: 'pull', repo: row.repo, number: row.number };
}

/**
 * The actions the API offers for one issue or PR, one button each, in the API's order and with its labels: the API
 * alone decides which exist. An action without text runs at once; one that needs text opens a text box first, one
 * that needs a model (set-model) opens the model picker first. One write at a time per group; `after` fires after every answer so the screen reloads (no update by hand).
 */
@Component({
  selector: 'app-action-buttons',
  imports: [MatButtonModule, MatProgressSpinnerModule],
  template: `
    @for (action of actions(); track action.code) {
      <button
        mat-stroked-button
        type="button"
        [attr.data-action]="action.code"
        [attr.aria-label]="action.label + ' (' + target().repo + ' #' + target().number + ')'"
        [disabled]="write.isBusy()"
        [attr.aria-busy]="write.isBusy() && runningCode() === action.code"
        (click)="start(action)"
      >
        @if (write.isBusy() && runningCode() === action.code) {
          <mat-progress-spinner class="app-busy" mode="indeterminate" diameter="16" aria-hidden="true" />
        }
        {{ action.label }}
      </button>
    }
  `,
  styleUrl: './action-buttons.scss',
  host: { '[class.compact]': 'compact()' },
})
export class ActionButtons {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);

  readonly actions = input.required<readonly ActionResponse[]>();
  readonly target = input.required<ActionTarget>();
  /** Smaller buttons for table rows and cards. */
  readonly compact = input(false, { transform: booleanAttribute });
  /** After every answer, success or failure: reload what the action changes. */
  readonly after = output<void>();

  /** The code of the action whose call runs, so only its button shows the spinner. */
  protected readonly runningCode = signal<string | null>(null);

  protected readonly write = writeAction({
    failure: 'The runner did not run this action.',
    after: () => this.after.emit(),
  });

  protected start(action: ActionResponse): void {
    if (action.needsModel) {
      this.openModelPicker(action);

      return;
    }

    if (action.needsText) {
      this.openTextBox(action);

      return;
    }

    void this.send(action, { text: null });
  }

  private openModelPicker(action: ActionResponse): void {
    const target = this.target();

    this.dialog.open<ActionModelDialog, ActionModelDialogData, boolean>(ActionModelDialog, {
      data: {
        title: action.label,
        target: `${target.repo} #${target.number}`,
        isBusy: this.write.isBusy,
        send: (workflow, model) => this.send(action, { text: null, workflow, model }),
      },
      width: 'min(480px, calc(100vw - 32px))',
      maxWidth: 'calc(100vw - 32px)',
    });
  }

  private openTextBox(action: ActionResponse): void {
    const target = this.target();

    this.dialog.open<ActionTextDialog, ActionTextDialogData, boolean>(ActionTextDialog, {
      data: {
        title: action.label,
        target: `${target.repo} #${target.number}`,
        isBusy: this.write.isBusy,
        send: (text) => this.send(action, { text }),
      },
      width: 'min(560px, calc(100vw - 32px))',
      maxWidth: 'calc(100vw - 32px)',
    });
  }

  private send(action: ActionResponse, body: ActionRequest): Promise<boolean> {
    this.runningCode.set(action.code);

    return this.write.run(() => this.call(action.code, body));
  }

  private call(code: string, body: ActionRequest): Promise<CommandResponse> {
    const target = this.target();
    const [owner, repo] = splitRepo(target.repo);
    const params = { owner, repo, number: target.number, code, body };

    if (target.kind === 'pull') {
      return this.api.invoke(runPullAction, params);
    }

    return this.api.invoke(runIssueAction, params);
  }
}
