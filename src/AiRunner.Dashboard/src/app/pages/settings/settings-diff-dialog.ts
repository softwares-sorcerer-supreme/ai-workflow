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
import { InlineNote } from '../../shared/inline-note/inline-note';
import { SettingChange } from './settings-form';

/** What the Save confirmation lists. */
export interface SettingsDiffData {
  changes: readonly SettingChange[];
  /** repos.yml holds comments, which this save drops. */
  hasComments: boolean;
  /** The repos.yml path, as the API sent it. */
  path: string;
}

/**
 * The confirmation before Save: every changed key with its old and new value, and (while repos.yml has comments) the
 * warning that the save writes the file without them. Built like `ConfirmDialog`: closes with `true` only on Save;
 * the dismiss button comes first and gets the focus.
 */
@Component({
  selector: 'app-settings-diff-dialog',
  imports: [InlineNote, MatButtonModule, MatDialogActions, MatDialogClose, MatDialogContent, MatDialogTitle],
  template: `
    <h2 mat-dialog-title>Save these changes to repos.yml?</h2>
    <mat-dialog-content>
      <p class="intro app-muted">
        The runner checks the whole file first, keeps a backup of the old one and applies the new settings at once.
      </p>

      @if (data.hasComments) {
        <app-inline-note data-testid="comments-warning">
          repos.yml has comments. The save writes the file again without them; their text is the help on this screen,
          and the backup keeps the old file.
        </app-inline-note>
      }

      <ul class="changes" data-testid="diff-list">
        @for (change of data.changes; track change.path) {
          <li class="change" [class]="'kind-' + change.kind" data-testid="diff-line">
            <span class="path app-mono" data-testid="diff-path">{{ change.path }}</span>
            @switch (change.kind) {
              @case ('added') {
                <span class="value" data-testid="diff-value">added: <span class="app-mono">{{ change.after }}</span></span>
              }
              @case ('removed') {
                <span class="value" data-testid="diff-value">removed (was <span class="app-mono">{{ change.before }}</span>)</span>
              }
              @default {
                <span class="value" data-testid="diff-value"
                  ><span class="app-mono">{{ change.before }}</span> &rarr; <span class="app-mono">{{ change.after }}</span></span
                >
              }
            }
          </li>
        }
      </ul>
      <p class="file app-muted app-mono">{{ data.path }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="confirm-dismiss">Keep editing</button>
      <button mat-flat-button type="button" [mat-dialog-close]="true" data-testid="confirm-accept">
        Save repos.yml
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .intro,
    .file {
      margin: 0 0 var(--app-space-3);
      font: var(--mat-sys-body-small);
    }

    .file {
      margin: var(--app-space-3) 0 0;
    }

    app-inline-note {
      margin-bottom: var(--app-space-3);
    }

    .changes {
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .change {
      display: flex;
      flex-direction: column;
      gap: var(--app-space-1);
      padding: var(--app-space-2) var(--app-space-3);
      border-left: 3px solid var(--mat-sys-outline-variant);
      font: var(--mat-sys-body-small);
    }

    .change + .change {
      margin-top: var(--app-space-1);
    }

    .kind-added {
      border-left-color: var(--app-tone-success-dot);
    }

    .kind-removed {
      border-left-color: var(--app-tone-danger-dot);
    }

    .kind-changed {
      border-left-color: var(--app-tone-info-dot);
    }

    .path {
      font: var(--mat-sys-label-large);
      font-family: var(--app-font-mono);
    }

    .value {
      overflow-wrap: anywhere;
    }

  `,
})
export class SettingsDiffDialog {
  protected readonly data = inject<SettingsDiffData>(MAT_DIALOG_DATA);
}

/** Opens the change list and resolves to true only when the person chose Save. */
export async function confirmSettingsSave(dialog: MatDialog, data: SettingsDiffData): Promise<boolean> {
  const dialogRef = dialog.open<SettingsDiffDialog, SettingsDiffData, boolean>(SettingsDiffDialog, {
    data,
    role: 'alertdialog',
    maxWidth: 'min(640px, calc(100vw - 32px))',
  });

  const result = await firstValueFrom(dialogRef.afterClosed());

  return result === true;
}
