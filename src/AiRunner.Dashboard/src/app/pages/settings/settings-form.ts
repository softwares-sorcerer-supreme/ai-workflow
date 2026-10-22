import { EffectiveValue } from '../../api/models/effective-value';
import { SettingDefinition } from '../../api/models/setting-definition';

/** A JSON value of repos.yml as the API sends it. */
export type JsonValue = null | boolean | number | string | JsonValue[] | JsonMap;

/** A JSON object of repos.yml (the file, a project, a component...). */
export interface JsonMap {
  [key: string]: JsonValue;
}

/** The scope codes of the schema the screen builds forms for (`legacy` is edited by hand). */
export type FormScope = 'global' | 'defaults' | 'project' | 'component';

/**
 * Where a form writes: the schema scope and the object of the file its keys live in (`[]` = the file root,
 * `['defaults']`, `['projects', 'shop']`, `['projects', 'shop', 'components', 'api']`).
 */
export interface ScopeTarget {
  scope: FormScope;
  root: readonly string[];
}

/** One field of a form: the schema entry and the full path of its value in the file. */
export interface SettingField {
  definition: SettingDefinition;
  /** Full path in the file: the scope's root plus the parts of the key. */
  segments: readonly string[];
  /** Stable id for `track` and element ids: the joined path. */
  id: string;
  /**
   * The key without the group and section parts (`max_diff_lines` in the group `limits`, `complex` in the section
   * `implement` of the group `models`).
   */
  label: string;
}

/**
 * The fields of a group that share their second key part, for keys of three or more parts (`models.implement.default`
 * and `models.implement.complex` form the section `implement` of the group `models`). Keys of two parts are in one
 * section without a name.
 */
export interface SettingSection {
  /** The second part of the key, or '' for the keys of two parts or less. */
  name: string;
  fields: SettingField[];
}

/** The fields of one group (the first part of the key: `copilot`, `limits`, `commands`...). */
export interface SettingGroup {
  /** The first part of the key, or `general` for keys without a dot. */
  name: string;
  title: string;
  /** Every field of the group, in the schema's order. */
  fields: SettingField[];
  /** The same fields by section, in the order their first key appears. */
  sections: SettingSection[];
}

/** One line of the key/value editor of a `string-map` field. */
export interface MapRow {
  name: string;
  value: string;
}

/** One line of the change list shown before Save. */
export interface SettingChange {
  /** Dotted path from the file root: `projects.shop.limits.max_diff_lines`. */
  path: string;
  kind: 'added' | 'removed' | 'changed';
  before?: string;
  after?: string;
}

/** The group of keys that have no dot (`poll_interval_seconds`, `workflows`...). */
export const GENERAL_GROUP = 'general';

/**
 * The fields of one scope, grouped by the first part of their key, in the schema's order (groups in the order their
 * first key appears). Built only from the schema, so a new key needs no change of the screen.
 */
export function buildGroups(
  schema: readonly SettingDefinition[],
  target: ScopeTarget,
): SettingGroup[] {
  const groups = new Map<string, SettingGroup>();

  for (const definition of schema) {
    if (definition.scope !== target.scope) {
      continue;
    }

    const keyParts = definition.key.split('.');
    const name = keyParts.length > 1 ? keyParts[0] : GENERAL_GROUP;
    const sectionName = keyParts.length > 2 ? keyParts[1] : '';
    const labelStart = keyParts.length > 2 ? 2 : 1;
    const segments = [...target.root, ...keyParts];
    const field: SettingField = {
      definition,
      segments,
      id: segments.join('.'),
      label: keyParts.length > 1 ? keyParts.slice(labelStart).join('.') : definition.key,
    };

    let group = groups.get(name);
    if (group === undefined) {
      group = { name, title: groupTitle(name), fields: [], sections: [] };
      groups.set(name, group);
    }

    let section = group.sections.find((entry) => entry.name === sectionName);
    if (section === undefined) {
      section = { name: sectionName, fields: [] };
      group.sections.push(section);
    }

    group.fields.push(field);
    section.fields.push(field);
  }

  return [...groups.values()];
}

/** `code_host` -> `Code host`: the heading of a group. */
export function groupTitle(name: string): string {
  const words = name.replace(/_/g, ' ');

  return words.charAt(0).toUpperCase() + words.slice(1);
}

export function isMap(value: JsonValue | undefined): value is JsonMap {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The value at a path, or undefined when any part is missing (= not written). */
export function valueAt(root: JsonValue | undefined, segments: readonly string[]): JsonValue | undefined {
  let node: JsonValue | undefined = root;

  for (const segment of segments) {
    if (!isMap(node) || !(segment in node)) {
      return undefined;
    }

    node = node[segment];
  }

  return node;
}

/** The names of the objects under a path (`projects` -> project names), in the file's order. */
export function namesAt(root: JsonValue | undefined, segments: readonly string[]): string[] {
  const node = valueAt(root, segments);

  return isMap(node) ? Object.keys(node) : [];
}

/** A copy of `root` with `value` written at the path; missing objects on the way are created. */
export function withValue(root: JsonMap, segments: readonly string[], value: JsonValue): JsonMap {
  if (segments.length === 0) {
    return root;
  }

  const [head, ...rest] = segments;
  const copy: JsonMap = { ...root };

  if (rest.length === 0) {
    copy[head] = value;
  } else {
    const child = root[head];
    copy[head] = withValue(isMap(child) ? child : {}, rest, value);
  }

  return copy;
}

/**
 * A copy of `root` without the value at the path (= not written, so it is inherited or built in). Objects the
 * removal leaves empty are removed too, but never the first `keep` parts of the path (the scope's own object: a
 * project stays even when it has no key left).
 */
export function withoutValue(root: JsonMap, segments: readonly string[], keep: number): JsonMap {
  if (segments.length === 0) {
    return root;
  }

  const [head, ...rest] = segments;
  if (!(head in root)) {
    return root;
  }

  const copy: JsonMap = { ...root };

  if (rest.length === 0) {
    delete copy[head];

    return copy;
  }

  const child = root[head];
  if (!isMap(child)) {
    return root;
  }

  const smaller = withoutValue(child, rest, keep - 1);
  if (Object.keys(smaller).length === 0 && keep <= 0) {
    delete copy[head];
  } else {
    copy[head] = smaller;
  }

  return copy;
}

/**
 * What changed between the file as read and the edited one, leaf by leaf (a list is one value). Presentation only:
 * the runner checks the whole file again on Save.
 */
export function diffSettings(before: JsonValue | undefined, after: JsonValue | undefined): SettingChange[] {
  const changes: SettingChange[] = [];
  collectChanges(before, after, [], changes);

  return changes;
}

function collectChanges(
  before: JsonValue | undefined,
  after: JsonValue | undefined,
  path: string[],
  changes: SettingChange[],
): void {
  const isBeforeMap = isMap(before);
  const isAfterMap = isMap(after);

  if ((isBeforeMap || before === undefined) && (isAfterMap || after === undefined) && (isBeforeMap || isAfterMap)) {
    const beforeMap = isBeforeMap ? before : {};
    const afterMap = isAfterMap ? after : {};
    const keys = [...Object.keys(beforeMap)];

    for (const key of Object.keys(afterMap)) {
      if (!keys.includes(key)) {
        keys.push(key);
      }
    }

    for (const key of keys) {
      collectChanges(beforeMap[key], afterMap[key], [...path, key], changes);
    }

    return;
  }

  const joined = path.join('.');

  if (before === undefined && after === undefined) {
    return;
  }

  if (before === undefined) {
    changes.push({ path: joined, kind: 'added', after: formatValue(after) });

    return;
  }

  if (after === undefined) {
    changes.push({ path: joined, kind: 'removed', before: formatValue(before) });

    return;
  }

  if (JSON.stringify(before) !== JSON.stringify(after)) {
    changes.push({ path: joined, kind: 'changed', before: formatValue(before), after: formatValue(after) });
  }
}

/** A value as one line of text: strings as written, lists as `a, b`, anything else as JSON. */
export function formatValue(value: JsonValue | undefined): string {
  if (value === undefined || value === null) {
    return 'none';
  }

  if (typeof value === 'string') {
    return value === '' ? '(empty)' : value;
  }

  if (Array.isArray(value)) {
    return value.length === 0 ? '(empty list)' : value.map((entry) => formatValue(entry)).join(', ');
  }

  if (isMap(value)) {
    const entries = Object.entries(value);

    return entries.length === 0
      ? '(empty map)'
      : entries.map(([key, entry]) => `${key}: ${formatValue(entry)}`).join(', ');
  }

  return JSON.stringify(value);
}

/** The lines of a `string-map` editor: one per entry of the written map, in the file's order. */
export function mapRows(value: JsonValue | undefined): MapRow[] {
  if (!isMap(value)) {
    return [];
  }

  return Object.entries(value).map(([name, entry]) => ({
    name,
    value: typeof entry === 'string' ? entry : formatValue(entry),
  }));
}

/**
 * The map the editor's lines mean: names trimmed, lines without a name left out (still being typed), a later line
 * with the same name wins. Undefined when no line is left, so the key is not written (the built-in empty map applies).
 */
export function rowsToMap(rows: readonly MapRow[]): JsonMap | undefined {
  const map: JsonMap = {};

  for (const row of rows) {
    const name = row.name.trim();
    if (name !== '') {
      map[name] = row.value.trim();
    }
  }

  return Object.keys(map).length === 0 ? undefined : map;
}

/** A deep copy of the file the API sent (the draft the form edits). */
export function cloneFile(file: object | null | undefined): JsonMap {
  if (file === null || file === undefined) {
    return {};
  }

  return JSON.parse(JSON.stringify(file)) as JsonMap;
}

/**
 * The line under a field that says what applies when the scope does not write the key. A project key shows the API's
 * inherited value (what applies without the project's own one, also right after an unsaved reset) with its label.
 * Everything else shows the schema's built-in value with the schema's label (the API words both labels).
 */
export function fallbackNote(definition: SettingDefinition, effective: EffectiveValue | undefined): string {
  if (effective !== undefined) {
    const inherited = effective.inherited;

    return `${inherited.sourceLabel}: ${formatValue(inherited.value as JsonValue)}`;
  }

  const label = definition.defaultLabel ?? '';
  if (definition.default === undefined || definition.default === null) {
    return label;
  }

  return `${label}: ${formatValue(definition.default as JsonValue)}`;
}

/** The value a toggle shows when the key is not written: the effective one, else the built-in one. */
export function fallbackFlag(definition: SettingDefinition, effective: EffectiveValue | undefined): boolean {
  if (effective !== undefined) {
    return effective.inherited.value === true;
  }

  return definition.default === true;
}

/** Text typed into a list box, one entry per line, as the list it means (blank lines dropped). */
export function parseLines(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/** The whole number typed into a number field, or undefined when it is not one. */
export function parseInteger(text: string): number | undefined {
  const trimmed = text.trim();
  if (!/^-?\d+$/.test(trimmed)) {
    return undefined;
  }

  return Number(trimmed);
}
