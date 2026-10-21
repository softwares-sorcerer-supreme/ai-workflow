/** One entry of the side menu. */
export interface NavItem {
  path: string;
  label: string;
  /** Material Symbols name. */
  icon: string;
}

/** The route of the Setup screen: the menu, the setup banner and the health strip all point there. */
export const SETUP_PATH = '/setup';

/** The route of the Agents screen: the health strip's "agents out of sync" chip points there. */
export const AGENTS_PATH = '/agents';

/** The side menu, in order. A screen that needs a parameter (issue detail) is reached from a list, not from here. */
export const NAV_ITEMS: readonly NavItem[] = [
  { path: '/overview', label: 'Overview', icon: 'dashboard' },
  { path: '/issues', label: 'Issues', icon: 'assignment' },
  { path: '/jobs', label: 'Jobs', icon: 'terminal' },
  { path: '/cost', label: 'Cost', icon: 'payments' },
  { path: '/workspaces', label: 'Workspaces', icon: 'folder_copy' },
  { path: AGENTS_PATH, label: 'Agents', icon: 'smart_toy' },
  { path: SETUP_PATH, label: 'Setup', icon: 'key' },
];
