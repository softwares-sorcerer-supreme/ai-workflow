import { Component, computed, input } from '@angular/core';
import { ViewState } from '../../shared/view-state/view-state';

/** How one line of a unified diff is drawn. */
export type DiffLineKind = 'add' | 'remove' | 'hunk' | 'meta' | 'context';

export interface DiffLine {
  kind: DiffLineKind;
  text: string;
}

/**
 * Splits a unified diff (`git diff` text from the API) into lines with the kind that picks their style. Only the
 * format's own first characters are read: file headers (`diff`, `index`, `---`, `+++`) and hunk headers (`@@`) are
 * muted, `+` lines added, `-` lines removed, everything else context. The text of every line stays as sent.
 */
export function parseDiff(diff: string): DiffLine[] {
  if (diff.length === 0) {
    return [];
  }

  const lines = diff.replace(/\r?\n$/, '').split(/\r?\n/);

  return lines.map((text) => ({ kind: kindOf(text), text }));
}

function kindOf(line: string): DiffLineKind {
  if (line.startsWith('@@')) {
    return 'hunk';
  }

  if (
    line.startsWith('diff ') ||
    line.startsWith('index ') ||
    line.startsWith('--- ') ||
    line.startsWith('+++ ') ||
    line.startsWith('\\')
  ) {
    return 'meta';
  }

  if (line.startsWith('+')) {
    return 'add';
  }

  if (line.startsWith('-')) {
    return 'remove';
  }

  return 'context';
}

/**
 * The diff of one agent file against git HEAD, as the runner sent it: monospace, added lines green, removed lines
 * red, headers muted (tone tokens only). An empty diff says the file has no changes against git.
 */
@Component({
  selector: 'app-agent-diff',
  imports: [ViewState],
  template: `
    @if (lines().length > 0) {
      <div
        class="diff app-mono"
        role="region"
        data-testid="agent-diff"
        aria-label="Changes against git"
      >
        @for (line of lines(); track $index) {
          <div [class]="'line ' + line.kind">{{ line.text || ' ' }}</div>
        }
      </div>
    } @else {
      <app-view-state
        kind="empty"
        icon="difference"
        title="No changes against git"
        message="The saved file is the same as in git HEAD."
      />
    }
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }

    .diff {
      max-height: 70vh;
      overflow: auto;
      padding: var(--app-space-2) 0;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: var(--app-radius-small);
      font: var(--mat-sys-body-small);
      font-family: var(--app-font-mono);
    }

    .line {
      padding: 0 var(--app-space-3);
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }

    .add {
      background: var(--app-tone-success-bg);
      color: var(--app-tone-success-fg);
    }

    .remove {
      background: var(--app-tone-danger-bg);
      color: var(--app-tone-danger-fg);
    }

    .hunk,
    .meta {
      color: var(--mat-sys-on-surface-variant);
    }

    .hunk {
      background: var(--mat-sys-surface-container-high);
    }
  `,
})
export class AgentDiff {
  /** The unified diff from `GET /api/agents/file`; empty when the file is unchanged. */
  readonly diff = input.required<string>();

  protected readonly lines = computed(() => parseDiff(this.diff()));
}
