import { ModelUse } from '../api/models/model-use';

/**
 * The model of a job as one line, joined from the API's fields: "claude-opus-5.5 - issue override". Without a model
 * the reason is the whole text (the API sends "Copilot default"); an old job without either shows "-".
 */
export function jobModelText(model: string | null | undefined, reason: string | null | undefined): string {
  const hasModel = model !== null && model !== undefined && model !== '';
  const hasReason = reason !== null && reason !== undefined && reason !== '';

  if (hasModel && hasReason) {
    return `${model} - ${reason}`;
  }

  if (hasModel) {
    return model;
  }

  return hasReason ? reason : '-';
}

/** One model of a cost row: "claude-opus-5.5 (issue override) x2"; without a model "Copilot default x2". */
export function modelUseText(use: ModelUse): string {
  const name = use.model === null || use.model === '' ? use.reason : `${use.model} (${use.reason})`;

  return `${name} x${use.jobs}`;
}

/** The models of a cost row as one line, in the API's order; empty when the row has none. */
export function modelUsesText(uses: readonly ModelUse[] | null | undefined): string {
  return (uses ?? []).map((use) => modelUseText(use)).join(', ');
}
