import { SettingDefinition } from '../../api/models/setting-definition';
import {
  JsonMap,
  buildGroups,
  diffSettings,
  fallbackNote,
  formatValue,
  namesAt,
  parseInteger,
  parseLines,
  valueAt,
  withValue,
  withoutValue,
} from './settings-form';

function definition(overrides: Partial<SettingDefinition>): SettingDefinition {
  return { path: '', key: '', scope: 'global', type: 'string', help: '', ...overrides };
}

const SCHEMA: SettingDefinition[] = [
  definition({ path: 'code_host.base_url', key: 'code_host.base_url' }),
  definition({ path: 'poll_interval_seconds', key: 'poll_interval_seconds', type: 'integer', default: 90 }),
  definition({ path: 'copilot.command', key: 'copilot.command', default: 'copilot' }),
  definition({ path: 'code_host.kind', key: 'code_host.kind', allowedValues: ['gitbucket'] }),
  definition({ path: 'defaults.limits.max_diff_lines', key: 'limits.max_diff_lines', scope: 'defaults' }),
  definition({ path: 'projects.*.limits.max_diff_lines', key: 'limits.max_diff_lines', scope: 'project' }),
  definition({ path: 'projects.*.commands.build', key: 'commands.build', scope: 'project' }),
  definition({ path: 'repos[].owner', key: 'owner', scope: 'legacy' }),
];

describe('buildGroups', () => {
  it('groups the keys of one scope by their first part, in the schema order', () => {
    const groups = buildGroups(SCHEMA, { scope: 'global', root: [] });

    expect(groups.map((group) => group.title)).toEqual(['Code host', 'General', 'Copilot']);
    expect(groups[0].fields.map((field) => field.label)).toEqual(['base_url', 'kind']);
    expect(groups[1].fields.map((field) => field.id)).toEqual(['poll_interval_seconds']);
  });

  it('puts the fields of a project under its object in the file', () => {
    const groups = buildGroups(SCHEMA, { scope: 'project', root: ['projects', 'shop'] });

    expect(groups.map((group) => group.name)).toEqual(['limits', 'commands']);
    expect(groups[0].fields[0].segments).toEqual(['projects', 'shop', 'limits', 'max_diff_lines']);
    expect(groups[1].fields[0].id).toBe('projects.shop.commands.build');
  });

  it('shows a new schema key without other changes, and nothing for a scope without keys', () => {
    const schema = [...SCHEMA, definition({ path: 'brand_new.thing', key: 'brand_new.thing', type: 'boolean' })];

    const groups = buildGroups(schema, { scope: 'global', root: [] });

    expect(groups.at(-1)?.title).toBe('Brand new');
    expect(buildGroups(schema, { scope: 'component', root: [] })).toEqual([]);
  });
});

describe('file paths', () => {
  const file: JsonMap = {
    defaults: { limits: { max_diff_lines: 800 } },
    projects: { shop: { limits: { max_diff_lines: 1200 }, components: { api: { repo: 'team/shop' } } } },
  };

  it('reads values and object names', () => {
    expect(valueAt(file, ['defaults', 'limits', 'max_diff_lines'])).toBe(800);
    expect(valueAt(file, ['defaults', 'review', 'scope'])).toBeUndefined();
    expect(namesAt(file, ['projects', 'shop', 'components'])).toEqual(['api']);
  });

  it('writes a value without changing the original, creating the objects on the way', () => {
    const next = withValue(file, ['projects', 'shop', 'review', 'scope'], 'all');

    expect(valueAt(next, ['projects', 'shop', 'review', 'scope'])).toBe('all');
    expect(valueAt(file, ['projects', 'shop', 'review'])).toBeUndefined();
  });

  it('removes a key and the objects it leaves empty, but keeps the scope object', () => {
    const next = withoutValue(file, ['projects', 'shop', 'limits', 'max_diff_lines'], 2);

    expect(next['projects']).toEqual({ shop: { components: { api: { repo: 'team/shop' } } } });
    expect(file['projects']).toEqual({
      shop: { limits: { max_diff_lines: 1200 }, components: { api: { repo: 'team/shop' } } },
    });
  });

  it('keeps an emptied project, but drops an emptied global group', () => {
    const project = withoutValue({ projects: { crm: { context: 'X.md' } } }, ['projects', 'crm', 'context'], 2);
    const global = withoutValue({ copilot: { command: 'copilot' }, runner_name: 'a' }, ['copilot', 'command'], 0);

    expect(project).toEqual({ projects: { crm: {} } });
    expect(global).toEqual({ runner_name: 'a' });
  });
});

describe('diffSettings', () => {
  it('lists added, removed and changed keys with their old and new values', () => {
    const before: JsonMap = {
      poll_interval_seconds: 90,
      projects: { shop: { limits: { max_diff_lines: 1200 }, workflows: ['analyze'] } },
      code_host: { web_password: { isSet: true } },
    };
    const after: JsonMap = {
      poll_interval_seconds: 120,
      projects: { shop: { workflows: ['analyze', 'review'] } },
      code_host: { web_password: { isSet: true } },
      copilot: { command: 'copilot.cmd' },
    };

    expect(diffSettings(before, after)).toEqual([
      { path: 'poll_interval_seconds', kind: 'changed', before: '90', after: '120' },
      { path: 'projects.shop.limits.max_diff_lines', kind: 'removed', before: '1200' },
      { path: 'projects.shop.workflows', kind: 'changed', before: 'analyze', after: 'analyze, review' },
      { path: 'copilot.command', kind: 'added', after: 'copilot.cmd' },
    ]);
  });

  it('finds nothing when the files are equal', () => {
    const file: JsonMap = { defaults: { never_edit: ['a/'] }, projects: { shop: {} } };

    expect(diffSettings(file, structuredClone(file))).toEqual([]);
  });
});

describe('field helpers', () => {
  it('says where an unwritten project key comes from', () => {
    const entry = definition({ key: 'limits.max_diff_lines', scope: 'project', default: 500 });

    const fromDefaults = { value: 800, source: 'defaults', sourceLabel: 'Inherited from defaults' };
    const builtIn = { value: 500, source: 'default', sourceLabel: 'Built-in default' };
    const own = { value: 1200, source: 'project', sourceLabel: 'Set in this project' };

    expect(fallbackNote(entry, { key: entry.key, effective: fromDefaults, inherited: fromDefaults })).toBe(
      'Inherited from defaults: 800',
    );
    expect(fallbackNote(entry, { key: entry.key, effective: builtIn, inherited: builtIn })).toBe(
      'Built-in default: 500',
    );
    // A project value reset but not saved yet: the note already shows what applies after the save.
    expect(fallbackNote(entry, { key: entry.key, effective: own, inherited: fromDefaults })).toBe(
      'Inherited from defaults: 800',
    );
    expect(fallbackNote(entry, undefined)).toBe('Built-in default: 500');
    expect(fallbackNote(definition({}), undefined)).toBe('Not set.');
  });

  it('parses list and number text', () => {
    expect(parseLines(' migrations/\n\nJenkinsfile \r\n')).toEqual(['migrations/', 'Jenkinsfile']);
    expect(parseInteger(' 42 ')).toBe(42);
    expect(parseInteger('4.5')).toBeUndefined();
    expect(parseInteger('ten')).toBeUndefined();
    expect(formatValue([])).toBe('(empty list)');
    expect(formatValue('')).toBe('(empty)');
  });
});
