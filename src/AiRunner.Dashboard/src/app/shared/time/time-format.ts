/**
 * Time text for the dashboard. The API sends UTC ISO-8601; people read local time. Pure functions, so they are
 * easy to test and the same everywhere.
 */

const timeOfDay = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const dateAndTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' });

/** Local time of day for today, local date and time otherwise. */
export function formatLocal(iso: string, nowMs: number): string {
  const date = new Date(iso);
  const now = new Date(nowMs);
  const isToday = date.toDateString() === now.toDateString();

  return isToday ? timeOfDay.format(date) : dateAndTime.format(date);
}

/** Full local date and time, for tooltips. */
export function formatFull(iso: string): string {
  return dateAndTime.format(new Date(iso));
}

/** "just now", "12 s ago", "5 min ago", "3 h ago", "2 d ago"; "in 5 min" for a future time. */
export function formatRelative(iso: string, nowMs: number): string {
  const differenceSeconds = Math.round((nowMs - new Date(iso).getTime()) / 1000);
  const isFuture = differenceSeconds < 0;
  const seconds = Math.abs(differenceSeconds);

  if (seconds < 5) {
    return 'just now';
  }

  const amount = shortAmount(seconds);

  return isFuture ? `in ${amount}` : `${amount} ago`;
}

/** A duration in seconds as "45 s", "3 min 05 s", "1 h 02 min", "2 d 3 h". */
export function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.round(totalSeconds));

  if (seconds < 60) {
    return `${seconds} s`;
  }

  if (seconds < 3600) {
    const minutes = Math.floor(seconds / 60);

    return `${minutes} min ${pad(seconds % 60)} s`;
  }

  if (seconds < 86400) {
    const hours = Math.floor(seconds / 3600);

    return `${hours} h ${pad(Math.floor((seconds % 3600) / 60))} min`;
  }

  const days = Math.floor(seconds / 86400);

  return `${days} d ${Math.floor((seconds % 86400) / 3600)} h`;
}

function shortAmount(seconds: number): string {
  if (seconds < 60) {
    return `${seconds} s`;
  }

  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)} min`;
  }

  if (seconds < 86400) {
    return `${Math.floor(seconds / 3600)} h`;
  }

  return `${Math.floor(seconds / 86400)} d`;
}

function pad(value: number): string {
  return value.toString().padStart(2, '0');
}
