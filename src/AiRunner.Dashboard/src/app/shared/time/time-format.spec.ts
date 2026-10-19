import { TEST_NOW as NOW } from '../testing';
import { formatDuration, formatRelative } from './time-format';
import { DurationPipe } from './duration.pipe';

describe('formatRelative', () => {
  it.each([
    ['2026-01-01T12:00:00Z', 'just now'],
    ['2026-01-01T11:59:48Z', '12 s ago'],
    ['2026-01-01T11:55:00Z', '5 min ago'],
    ['2026-01-01T09:00:00Z', '3 h ago'],
    ['2025-12-30T12:00:00Z', '2 d ago'],
    ['2026-01-01T12:05:00Z', 'in 5 min'],
  ])('%s is "%s"', (iso, expected) => {
    expect(formatRelative(iso, NOW)).toBe(expected);
  });
});

describe('formatDuration', () => {
  it.each([
    [45, '45 s'],
    [185, '3 min 05 s'],
    [3720, '1 h 02 min'],
    [183600, '2 d 3 h'],
  ])('%d s is "%s"', (seconds, expected) => {
    expect(formatDuration(seconds)).toBe(expected);
  });

  it('shows "-" for a missing value (old rows)', () => {
    const pipe = new DurationPipe();

    expect(pipe.transform(null)).toBe('-');
    expect(pipe.transform(undefined)).toBe('-');
    expect(pipe.transform(90)).toBe('1 min 30 s');
  });
});
