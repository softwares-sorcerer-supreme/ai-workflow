import {
  ISSUE_MARK_TONES,
  JOB_STATUS_TONES,
  issueMarkIcon,
  issueMarkTone,
  jobStatusTone,
} from './tone';

describe('issueMarkTone and issueMarkIcon', () => {
  it.each([
    ['failed', 'danger', undefined],
    ['claimed', 'neutral', 'lock'],
    ['on-github', 'neutral', 'cloud'],
    ['queued', 'progress', 'schedule'],
    ['running', 'progress', 'play_circle'],
  ])('shows %s as %s with icon %s', (code, tone, icon) => {
    expect(issueMarkTone(code)).toBe(tone);
    expect(issueMarkIcon(code)).toBe(icon);
  });

  it('shows an unknown flag as neutral without an icon', () => {
    expect(issueMarkTone('paused')).toBe('neutral');
    expect(issueMarkIcon('paused')).toBeUndefined();
  });

  it('has a color for every flag and badge the API sends', () => {
    expect(Object.keys(ISSUE_MARK_TONES).sort()).toEqual(['claimed', 'failed', 'on-github', 'queued', 'running']);
  });
});

describe('jobStatusTone', () => {
  it.each([
    ['pending', 'progress'],
    ['running', 'progress'],
    ['done', 'success'],
    ['failed', 'danger'],
  ])('colors job status %s with %s', (code, tone) => {
    expect(jobStatusTone(code)).toBe(tone);
  });

  it('shows an unknown status as neutral', () => {
    expect(jobStatusTone('paused')).toBe('neutral');
  });

  it('has a color for every job status the API sends', () => {
    expect(Object.keys(JOB_STATUS_TONES).sort()).toEqual(['done', 'failed', 'pending', 'running']);
  });
});
