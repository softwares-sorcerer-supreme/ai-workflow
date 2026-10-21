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
const HEALTH_TONES: Readonly<Record<string, Tone>> = {
  ok: 'success',
  starting: 'info',
  stale: 'warning',
  // Paused from the dashboard: a person must resume it before a new job starts.
  paused: 'warning',
  error: 'danger',
};

/** Color per job status code (C# JobStatus). Queued and running are both "the runner is on it". */
export const JOB_STATUS_TONES: Readonly<Record<string, Tone>> = {
  pending: 'progress',
  running: 'progress',
  done: 'success',
  failed: 'danger',
};

/**
 * Color per issue flag and badge code (C# `Core/IssueMarks`): the flags `failed`, `claimed`, `on-github` next to the
 * stage, and the badges `queued`, `running` when this runner has a job for the issue.
 */
export const ISSUE_MARK_TONES: Readonly<Record<string, Tone>> = {
  failed: 'danger',
  claimed: 'neutral',
  'on-github': 'neutral',
  queued: 'progress',
  running: 'progress',
};

/** Icon per issue flag and badge code; a code without one shows the tone dot. */
export const ISSUE_MARK_ICONS: Readonly<Record<string, string>> = {
  claimed: 'lock',
  'on-github': 'cloud',
  queued: 'schedule',
  running: 'play_circle',
};

/** Icon per worker state code (`idle` or `busy`). */
const WORKER_ICONS: Readonly<Record<string, string>> = {
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

/** The color of an issue flag or badge code, neutral for an unknown one. */
export function issueMarkTone(code: string): Tone {
  return ISSUE_MARK_TONES[code] ?? 'neutral';
}

/** The icon of an issue flag or badge code, or undefined (the chip then shows its dot). */
export function issueMarkIcon(code: string): string | undefined {
  return ISSUE_MARK_ICONS[code];
}

/** The color of a job status code, neutral for an unknown one. */
export function jobStatusTone(code: string): Tone {
  return JOB_STATUS_TONES[code] ?? 'neutral';
}

/**
 * Color per lane code (C# `Core/IssueLane`): the five columns of the board and segments of the pipeline summary. The
 * API decides the lane of every issue and sends the lanes in board order; this map only picks a color.
 */
export const LANE_TONES: Readonly<Record<string, Tone>> = {
  todo: 'neutral',
  ready: 'info',
  'in-progress': 'progress',
  'in-review': 'info',
  finished: 'success',
};

/** Color per focus code (C# `Core/IssueFocus`): the quick filters "Needs you", "Runner working" and "Failed". */
export const FOCUS_TONES: Readonly<Record<string, Tone>> = {
  'needs-you': 'warning',
  working: 'progress',
  failed: 'danger',
};

/** Color per pull request state code the API sends (`open`, `merged`, `closed` without a merge). */
export const PULL_STATE_TONES: Readonly<Record<string, Tone>> = {
  open: 'info',
  merged: 'success',
  closed: 'neutral',
};

/** The color of a lane code, neutral for an unknown one. */
export function laneTone(code: string): Tone {
  return LANE_TONES[code] ?? 'neutral';
}

/** The color of a focus code, neutral for an unknown one. */
export function focusTone(code: string): Tone {
  return FOCUS_TONES[code] ?? 'neutral';
}

/** The color of a pull request state code, neutral for an unknown one. */
export function pullStateTone(code: string): Tone {
  return PULL_STATE_TONES[code] ?? 'neutral';
}
