/**
 * The status colors of the dashboard. Every chip, dot and banner uses one of these; the colors themselves are the
 * `--app-tone-<tone>-{bg,fg,dot}` tokens in styles.scss. See UI.md for which tone means what.
 */
export type Tone = 'neutral' | 'info' | 'progress' | 'success' | 'warning' | 'danger';
