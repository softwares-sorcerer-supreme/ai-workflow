import { JOB_STATUS_TONES, jobStatusTone } from './tone';

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
