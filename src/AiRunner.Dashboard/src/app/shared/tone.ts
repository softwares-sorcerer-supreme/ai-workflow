/**
 * The status colors of the dashboard. Every chip, dot and banner uses one of these; the colors themselves are the
 * `--app-tone-<tone>-{bg,fg,dot}` tokens in styles.scss. See UI.md for which tone means what.
 */
export type Tone = 'neutral' | 'info' | 'progress' | 'success' | 'warning' | 'danger';

/**
 * Color per stage code. The API computes the stage (C# Core/IssueStage) and sends its code and label; this map only
 * picks a color.
 */
export const STAGE_TONES: Readonly<Record<string, Tone>> = {
  new: 'neutral',
  'questions-open': 'warning',
  'ready-simple': 'info',
  'ready-complex': 'info',
  implementing: 'progress',
  'broken-down': 'progress',
  'pr-open': 'info',
  merged: 'success',
  done: 'success',
  'closed-no-merge': 'neutral',
};

/** Color per health code. The runner decides the state (C# Core/RunnerHealth); this only picks the color. */
export const HEALTH_TONES: Readonly<Record<string, Tone>> = {
  ok: 'success',
  starting: 'info',
  stale: 'warning',
  error: 'danger',
};

/** Icon per worker state code (`idle` or `busy`). */
export const WORKER_ICONS: Readonly<Record<string, string>> = {
  idle: 'pause_circle',
  busy: 'play_circle',
};

/** The color of a stage code. A code that is not in the map is neutral, so a new stage never breaks a screen. */
export function stageTone(code: string): Tone {
  return STAGE_TONES[code] ?? 'neutral';
}

/** The color of a health code, neutral for an unknown or missing one. */
export function healthTone(code: string | undefined): Tone {
  return (code ? HEALTH_TONES[code] : undefined) ?? 'neutral';
}

/** The icon of a worker state code, a neutral dot-like icon for an unknown one. */
export function workerIcon(code: string): string {
  return WORKER_ICONS[code] ?? 'pause_circle';
}
