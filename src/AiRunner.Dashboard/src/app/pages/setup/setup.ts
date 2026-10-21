import { Component, inject, linkedSignal, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Api } from '../../api/api';
import { getSetup } from '../../api/fn/setup/get-setup';
import { removeToken } from '../../api/fn/setup/remove-token';
import { removeWebPassword } from '../../api/fn/setup/remove-web-password';
import { saveToken } from '../../api/fn/setup/save-token';
import { saveWebPassword } from '../../api/fn/setup/save-web-password';
import { SetupChangeResponse } from '../../api/models/setup-change-response';
import { SetupResponse } from '../../api/models/setup-response';
import { HealthStore } from '../../layout/health-store';
import { confirmWrite } from '../../shared/confirm-dialog/confirm-dialog';
import { InlineNote } from '../../shared/inline-note/inline-note';
import { PageHeader } from '../../shared/page-header/page-header';
import { pollResource } from '../../shared/poll-resource';
import { StatusChip } from '../../shared/status-chip/status-chip';
import { Tone } from '../../shared/tone';
import { ViewState } from '../../shared/view-state/view-state';
import { writeAction } from '../../shared/write-action';

/**
 * The chip tone of a secret source code (`tokenSource`, `webPasswordSource`). Only the style: the label and the
 * "change it there" note come from the server (`*SourceLabel`, `*Note`). An unknown code is neutral.
 */
const SOURCE_TONES: Readonly<Record<string, Tone>> = {
  'user-secrets': 'success',
  environment: 'info',
  'repos-yml': 'info',
};

/** A missing token stops the runner (warning); a missing web password only stops closing issues (neutral). */
function sourceTone(code: string, isRequired: boolean): Tone {
  if (code === 'none') {
    return isRequired ? 'warning' : 'neutral';
  }

  return SOURCE_TONES[code] ?? 'neutral';
}

export const REMOVE_TOKEN_MESSAGE =
  'The runner deletes the token from the user secrets file. Unless the GITBUCKET_TOKEN environment variable is ' +
  'set, it then polls nothing and runs no job until you save a token here again.';

export const REMOVE_WEB_PASSWORD_MESSAGE =
  'The runner deletes the web password from the user secrets file. Unless repos.yml has one, it can no longer ' +
  'close issues after a merge until you save the password here again.';

/**
 * The runner's GitBucket credentials: the token (every API and git call) and the web password (closing issues after
 * a merge). The secrets are only ever sent to the runner, which checks them before it saves them; the screen never
 * gets one back, so the password fields always start empty and are cleared after every attempt.
 */
@Component({
  selector: 'app-setup',
  imports: [
    InlineNote,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    PageHeader,
    StatusChip,
    ViewState,
  ],
  templateUrl: './setup.html',
  styleUrl: './setup.scss',
})
export class Setup {
  private readonly api = inject(Api);
  private readonly dialog = inject(MatDialog);
  private readonly health = inject(HealthStore).health;

  protected readonly resource = pollResource(() => this.api.invoke(getSetup));

  /** What is shown: the last poll, or the `setup` a write answered with until the next poll replaces it. */
  protected readonly current = linkedSignal<SetupResponse | undefined>(() => this.resource.value());

  /** Typed secrets. Plain component state: gone when the screen is left, emptied as soon as a call is sent. */
  protected readonly token = signal('');
  protected readonly webPassword = signal('');

  protected readonly tokenSave = writeAction({
    failure: 'The runner did not save the token.',
    after: () => this.reload(),
  });
  protected readonly tokenRemove = writeAction({
    failure: 'The runner did not remove the token.',
    after: () => this.reload(),
  });
  protected readonly webPasswordSave = writeAction({
    failure: 'The runner did not save the web password.',
    after: () => this.reload(),
  });
  protected readonly webPasswordRemove = writeAction({
    failure: 'The runner did not remove the web password.',
    after: () => this.reload(),
  });

  protected readonly tokenTone = (code: string) => sourceTone(code, true);
  protected readonly webPasswordTone = (code: string) => sourceTone(code, false);

  protected setToken(event: Event): void {
    this.token.set((event.target as HTMLInputElement).value);
  }

  protected setWebPassword(event: Event): void {
    this.webPassword.set((event.target as HTMLInputElement).value);
  }

  protected submitToken(event: Event): void {
    event.preventDefault();

    const token = this.token();
    if (!token || this.tokenSave.isBusy()) {
      return;
    }

    this.token.set('');
    void this.tokenSave.run(() => this.change(this.api.invoke(saveToken, { body: { token } })));
  }

  protected submitWebPassword(event: Event): void {
    event.preventDefault();

    const password = this.webPassword();
    if (!password || this.webPasswordSave.isBusy()) {
      return;
    }

    this.webPassword.set('');
    void this.webPasswordSave.run(() =>
      this.change(this.api.invoke(saveWebPassword, { body: { password } })),
    );
  }

  protected async removeToken(): Promise<void> {
    const isConfirmed = await confirmWrite(this.dialog, {
      title: 'Remove the saved token?',
      message: REMOVE_TOKEN_MESSAGE,
      confirmLabel: 'Remove token',
      dismissLabel: 'Keep token',
    });

    if (!isConfirmed) {
      return;
    }

    await this.tokenRemove.run(() => this.change(this.api.invoke(removeToken)));
  }

  protected async removeWebPassword(): Promise<void> {
    const isConfirmed = await confirmWrite(this.dialog, {
      title: 'Remove the saved web password?',
      message: REMOVE_WEB_PASSWORD_MESSAGE,
      confirmLabel: 'Remove password',
      dismissLabel: 'Keep password',
    });

    if (!isConfirmed) {
      return;
    }

    await this.webPasswordRemove.run(() => this.change(this.api.invoke(removeWebPassword)));
  }

  /** Shows the `setup` the runner answered with (its own state after the write, never a guess of ours). */
  private async change(call: Promise<SetupChangeResponse>): Promise<SetupChangeResponse> {
    const answer = await call;
    this.current.set(answer.setup);

    return answer;
  }

  /** A write can end setup mode: re-read the health too, so the banner and the strip follow at once. */
  private reload(): void {
    this.resource.reload();
    this.health.reload();
  }
}
