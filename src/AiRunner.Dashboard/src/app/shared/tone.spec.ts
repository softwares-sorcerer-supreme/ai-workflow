import {
  FOCUS_TONES,
  ISSUE_MARK_TONES,
  JOB_STATUS_TONES,
  LANE_TONES,
  PULL_STATE_TONES,
  STEP_STATE_TONES,
  WORKSPACE_WARNING_ICONS,
  focusTone,
  healthTone,
  issueMarkIcon,
  issueMarkTone,
  jobStatusTone,
  laneTone,
  pullStateTone,
  stepStateIcon,
  stepStateTone,
  workspaceWarningIcon,
  agentStateTone,
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

describe('laneTone', () => {
  it.each([
    ['todo', 'neutral'],
    ['ready', 'info'],
    ['in-progress', 'progress'],
    ['in-review', 'info'],
    ['finished', 'success'],
  ])('colors lane %s with %s', (code, tone) => {
    expect(laneTone(code)).toBe(tone);
  });

  it('shows an unknown lane as neutral', () => {
    expect(laneTone('parked')).toBe('neutral');
  });

  it('has a color for every lane the API sends', () => {
    expect(Object.keys(LANE_TONES).sort()).toEqual(['finished', 'in-progress', 'in-review', 'ready', 'todo']);
  });
});

describe('focusTone', () => {
  it.each([
    ['needs-you', 'warning'],
    ['working', 'progress'],
    ['failed', 'danger'],
  ])('colors focus %s with %s', (code, tone) => {
    expect(focusTone(code)).toBe(tone);
  });

  it('shows an unknown focus as neutral', () => {
    expect(focusTone('stale')).toBe('neutral');
  });

  it('has a color for every focus the API sends', () => {
    expect(Object.keys(FOCUS_TONES).sort()).toEqual(['failed', 'needs-you', 'working']);
  });
});

describe('pullStateTone', () => {
  it.each([
    ['open', 'info'],
    ['merged', 'success'],
    ['closed', 'neutral'],
  ])('colors pull state %s with %s', (code, tone) => {
    expect(pullStateTone(code)).toBe(tone);
  });

  it('shows an unknown pull state as neutral', () => {
    expect(pullStateTone('draft')).toBe('neutral');
  });

  it('has a color for every pull state the API sends', () => {
    expect(Object.keys(PULL_STATE_TONES).sort()).toEqual(['closed', 'merged', 'open']);
  });
});

describe('healthTone', () => {
  it.each([
    ['ok', 'success'],
    ['starting', 'info'],
    ['stale', 'warning'],
    ['paused', 'warning'],
    ['needs-setup', 'warning'],
    ['error', 'danger'],
  ])('shows %s as %s', (code, tone) => {
    expect(healthTone(code)).toBe(tone);
  });

  it('shows an unknown or missing state as neutral', () => {
    expect(healthTone('something-new')).toBe('neutral');
    expect(healthTone(undefined)).toBe('neutral');
  });
});

describe('stepStateTone and stepStateIcon', () => {
  it.each([
    ['running', 'progress', 'progress_activity'],
    ['ok', 'success', 'check_circle'],
    ['failed', 'danger', 'error'],
    ['interrupted', 'warning', 'cancel'],
  ])('shows step state %s as %s with icon %s', (code, tone, icon) => {
    expect(stepStateTone(code)).toBe(tone);
    expect(stepStateIcon(code)).toBe(icon);
  });

  it('shows an unknown step state as neutral without an icon', () => {
    expect(stepStateTone('skipped')).toBe('neutral');
    expect(stepStateIcon('skipped')).toBeUndefined();
  });

  it('has a color for every step state the API sends', () => {
    expect(Object.keys(STEP_STATE_TONES).sort()).toEqual(['failed', 'interrupted', 'ok', 'running']);
  });
});

describe('workspaceWarningIcon', () => {
  it.each([
    ['agent-override', 'smart_toy'],
    ['instructions-missing', 'description'],
    ['has-changes', 'edit_document'],
  ])('shows warning %s with icon %s', (code, icon) => {
    expect(workspaceWarningIcon(code)).toBe(icon);
  });

  it('shows an unknown warning with the plain warning icon', () => {
    expect(workspaceWarningIcon('something-new')).toBe('warning');
  });

  it('has an icon for every warning the API sends', () => {
    expect(Object.keys(WORKSPACE_WARNING_ICONS).sort()).toEqual([
      'agent-override',
      'has-changes',
      'instructions-missing',
    ]);
  });
});

describe('agentStateTone', () => {
  it.each([
    ['unchanged', 'neutral'],
    ['modified', 'info'],
    ['untracked', 'warning'],
    ['unknown', 'neutral'],
    ['in-sync', 'success'],
    ['differs', 'warning'],
    ['missing', 'warning'],
    ['not-installed', 'neutral'],
  ])('shows state %s in tone %s', (code, tone) => {
    expect(agentStateTone(code)).toBe(tone);
  });

  it('shows an unknown state as neutral', () => {
    expect(agentStateTone('something-new')).toBe('neutral');
  });
});
