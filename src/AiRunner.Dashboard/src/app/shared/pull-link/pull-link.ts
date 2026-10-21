import { Component, computed, input } from '@angular/core';
import { PullResponse } from '../../api/models/pull-response';
import { Tone, pullStateTone } from '../tone';

/**
 * One pull request as a small "#50" pill, colored by the state the API sent (`PULL_STATE_TONES`) and opening the PR
 * on GitBucket in a new tab. The state label and the title are in the hover text and the accessible name, so the
 * color is never the only signal.
 */
@Component({
  selector: 'app-pull-link',
  template: `
    @if (pull().htmlUrl; as url) {
      <a
        class="pill"
        [href]="url"
        target="_blank"
        rel="noopener noreferrer"
        [attr.title]="hint()"
        [attr.aria-label]="name()"
        >#{{ pull().number }}</a
      >
    } @else {
      <span class="pill" [attr.title]="hint()" [attr.aria-label]="name()">#{{ pull().number }}</span>
    }
  `,
  styleUrl: './pull-link.scss',
  host: {
    '[class]': '"tone-" + tone()',
    '[attr.data-state]': 'pull().state',
  },
})
export class PullLink {
  readonly pull = input.required<PullResponse>();

  protected readonly tone = computed<Tone>(() => pullStateTone(this.pull().state));

  /** "team/shop #50 Export orders (Merged)". */
  protected readonly hint = computed(() => {
    const pull = this.pull();

    return `${pull.repo} #${pull.number} ${pull.title} (${pull.stateLabel})`;
  });

  /** "Pull request #50, Merged". */
  protected readonly name = computed(() => {
    const pull = this.pull();

    return `Pull request #${pull.number}, ${pull.stateLabel}`;
  });
}
