import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogTitle,
} from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';

/** What the confirmation asks. Every text is a full, plain sentence or a verb for the button. */
export interface ConfirmDialogData {
  /** A question: "Cancel the running job?" */
  title: string;
  /** What happens, in plain words, including what the person must do afterwards. */
  message: string;
  /** The verb of the destructive button: "Cancel job". */
  confirmLabel: string;
  /** The button that keeps things as they are: "Keep running". */
  dismissLabel: string;
}

/**
 * The confirmation before a destructive write. Closes with `true` only on the confirm button; Escape, a click
 * outside and the dismiss button close it with nothing, so nothing is written. The dismiss button comes first, so it
 * gets the focus when the dialog opens.
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [MatButtonModule, MatDialogActions, MatDialogClose, MatDialogContent, MatDialogTitle],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="message">{{ data.message }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="confirm-dismiss">
        {{ data.dismissLabel }}
      </button>
      <button mat-flat-button type="button" [mat-dialog-close]="true" data-testid="confirm-accept">
        {{ data.confirmLabel }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .message {
      margin: 0;
      font: var(--mat-sys-body-medium);
    }
  `,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);
}

/** Opens the confirmation and resolves to true only when the person confirmed. */
export async function confirmWrite(dialog: MatDialog, data: ConfirmDialogData): Promise<boolean> {
  const dialogRef = dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
    data,
    role: 'alertdialog',
    maxWidth: 'min(480px, calc(100vw - 32px))',
  });

  const result = await firstValueFrom(dialogRef.afterClosed());

  return result === true;
}
