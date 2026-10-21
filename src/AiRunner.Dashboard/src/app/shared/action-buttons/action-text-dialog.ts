import { Component, Signal, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

/** What the text box of an action shows and how it sends. */
export interface ActionTextDialogData {
  /** The action's label as the API sent it ("Answer the questions"). */
  title: string;
  /** Where it goes, joined from the API's fields ("team/shop #42"). */
  target: string;
  /** True while the group's write runs (from its `writeAction`). */
  isBusy: Signal<boolean>;
  /** Sends the text; resolves to true when the runner accepted it. */
  send: (text: string) => Promise<boolean>;
}

/**
 * The text box of an action that needs text (answer, ask, fix): a multi-line field, Send disabled while it is blank
 * or while the call runs. It closes only when the runner accepted the text; on a refusal it stays open with the text,
 * and the runner's message shows in the snack bar. Cancel and Escape send nothing.
 */
@Component({
  selector: 'app-action-text-dialog',
  imports: [
    MatButtonModule,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="target app-muted">{{ data.target }}</p>
      <mat-form-field appearance="outline" class="field">
        <mat-label>Text</mat-label>
        <textarea
          matInput
          rows="6"
          data-testid="action-text"
          [value]="text()"
          (input)="text.set($any($event.target).value)"
        ></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="action-text-cancel">Cancel</button>
      <button
        mat-flat-button
        type="button"
        data-testid="action-text-send"
        [disabled]="!canSend()"
        [attr.aria-busy]="data.isBusy()"
        (click)="send()"
      >
        @if (data.isBusy()) {
          <mat-progress-spinner class="app-busy" mode="indeterminate" diameter="18" aria-hidden="true" />
        }
        Send
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .target {
      margin: 0 0 var(--app-space-3);
      font: var(--mat-sys-body-small);
    }

    .field {
      width: 100%;
    }
  `,
})
export class ActionTextDialog {
  protected readonly data = inject<ActionTextDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject<MatDialogRef<ActionTextDialog, boolean>>(MatDialogRef);

  protected readonly text = signal('');
  protected readonly canSend = computed(() => this.text().trim() !== '' && !this.data.isBusy());

  protected async send(): Promise<void> {
    if (!this.canSend()) {
      return;
    }

    const isAccepted = await this.data.send(this.text());

    if (isAccepted) {
      this.dialogRef.close(true);
    }
  }
}
