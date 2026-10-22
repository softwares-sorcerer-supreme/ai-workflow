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
import { MatSelectModule } from '@angular/material/select';
import { Api } from '../../api/api';
import { getModelOptions } from '../../api/fn/issues/get-model-options';
import { ModelOptionsResponse } from '../../api/models/model-options-response';
import { describeError } from '../poll-resource';
import { ViewState } from '../view-state/view-state';

/** What the model picker of an action shows and how it sends. */
export interface ActionModelDialogData {
  /** The action's label as the API sent it ("Set model"). */
  title: string;
  /** Where it goes, joined from the API's fields ("team/shop #42"). */
  target: string;
  /** True while the group's write runs (from its `writeAction`). */
  isBusy: Signal<boolean>;
  /** Sends the choice (an empty model removes the override); resolves to true when the runner accepted it. */
  send: (workflow: string, model: string) => Promise<boolean>;
}

/**
 * The picker of an action that needs a model (set-model): a workflow and a model, both offered by
 * `GET /api/model-options` (read once when the box opens). The model is a select of `allowedModels` when the runner
 * has that list, else a text box; empty means "remove the override". It closes only when the runner accepted the
 * choice; on a refusal it stays open, and the runner's message shows in the snack bar. Cancel and Escape send nothing.
 */
@Component({
  selector: 'app-action-model-dialog',
  imports: [
    MatButtonModule,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    ViewState,
  ],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="target app-muted">{{ data.target }}</p>

      @if (options(); as choices) {
        <mat-form-field appearance="outline" class="field">
          <mat-label>Workflow</mat-label>
          <mat-select
            data-testid="model-workflow"
            [value]="workflow()"
            (selectionChange)="workflow.set($event.value)"
          >
            @for (option of choices.workflows; track option.code) {
              <mat-option [value]="option.code">{{ option.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        @if (choices.allowedModels.length > 0) {
          <mat-form-field appearance="outline" class="field">
            <mat-label>Model</mat-label>
            <mat-select
              data-testid="model-select"
              [value]="model()"
              (selectionChange)="model.set($event.value)"
            >
              <mat-option value="">No override (remove it)</mat-option>
              @for (option of choices.allowedModels; track option) {
                <mat-option [value]="option">{{ option }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        } @else {
          <mat-form-field appearance="outline" class="field">
            <mat-label>Model</mat-label>
            <input
              matInput
              type="text"
              spellcheck="false"
              data-testid="model-input"
              placeholder="claude-sonnet-5.5"
              [value]="model()"
              (input)="model.set($any($event.target).value)"
            />
            <mat-hint>Leave empty to remove the override.</mat-hint>
          </mat-form-field>
        }
      } @else if (loadError(); as message) {
        <app-view-state
          kind="error"
          title="Could not load the models"
          [message]="message"
          [canRetry]="true"
          (retry)="load()"
        />
      } @else {
        <app-view-state kind="loading" />
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close data-testid="model-cancel">Cancel</button>
      <button
        mat-flat-button
        type="button"
        data-testid="model-send"
        [disabled]="!canSend()"
        [attr.aria-busy]="data.isBusy()"
        (click)="send()"
      >
        @if (data.isBusy()) {
          <mat-progress-spinner class="app-busy" mode="indeterminate" diameter="18" aria-hidden="true" />
        }
        Save
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
export class ActionModelDialog {
  protected readonly data = inject<ActionModelDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject<MatDialogRef<ActionModelDialog, boolean>>(MatDialogRef);
  private readonly api = inject(Api);

  /** The runner's choices; undefined until they arrive. */
  protected readonly options = signal<ModelOptionsResponse | undefined>(undefined);
  protected readonly loadError = signal<string | undefined>(undefined);

  protected readonly workflow = signal('');
  protected readonly model = signal('');

  protected readonly canSend = computed(
    () => this.options() !== undefined && this.workflow() !== '' && !this.data.isBusy(),
  );

  constructor() {
    void this.load();
  }

  /** Reads the choices once (the box is short-lived, so it is not polled). */
  protected async load(): Promise<void> {
    this.loadError.set(undefined);

    try {
      const answer = await this.api.invoke(getModelOptions);
      this.options.set(answer);
    } catch (failure) {
      this.loadError.set(describeError(failure, 'The runner did not send the models.'));
    }
  }

  protected async send(): Promise<void> {
    if (!this.canSend()) {
      return;
    }

    const isAccepted = await this.data.send(this.workflow(), this.model());

    if (isAccepted) {
      this.dialogRef.close(true);
    }
  }
}
