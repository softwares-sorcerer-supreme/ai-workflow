import { jobModelText, modelUseText, modelUsesText } from './model-text';

describe('jobModelText', () => {
  it('joins the model and the reason', () => {
    expect(jobModelText('claude-opus-5.5', 'issue override')).toBe('claude-opus-5.5 - issue override');
  });

  it('shows the reason alone when there is no model', () => {
    expect(jobModelText(null, 'Copilot default')).toBe('Copilot default');
  });

  it('shows the model alone when there is no reason', () => {
    expect(jobModelText('claude-sonnet-5.5', null)).toBe('claude-sonnet-5.5');
  });

  it('shows a dash for an old job with neither', () => {
    expect(jobModelText(null, null)).toBe('-');
    expect(jobModelText(undefined, undefined)).toBe('-');
  });
});

describe('modelUseText', () => {
  it('shows the model, its reason and the job count', () => {
    expect(modelUseText({ model: 'claude-opus-5.5', reason: 'issue override', jobs: 2 })).toBe(
      'claude-opus-5.5 (issue override) x2',
    );
  });

  it('shows the reason alone without a model', () => {
    expect(modelUseText({ model: null, reason: 'Copilot default', jobs: 1 })).toBe('Copilot default x1');
  });
});

describe('modelUsesText', () => {
  it('joins the uses in the given order', () => {
    expect(
      modelUsesText([
        { model: 'claude-opus-5.5', reason: 'complex issue', jobs: 3 },
        { model: null, reason: 'Copilot default', jobs: 1 },
      ]),
    ).toBe('claude-opus-5.5 (complex issue) x3, Copilot default x1');
  });

  it('is empty without uses', () => {
    expect(modelUsesText([])).toBe('');
    expect(modelUsesText(undefined)).toBe('');
  });
});
