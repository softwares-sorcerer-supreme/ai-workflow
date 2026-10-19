/** One entry of the side menu. */
export interface NavItem {
  path: string;
  label: string;
  /** Material Symbols name. */
  icon: string;
}

/** The side menu, in order. A screen that needs a parameter (issue detail) is reached from a list, not from here. */
export const NAV_ITEMS: readonly NavItem[] = [
  { path: '/overview', label: 'Overview', icon: 'dashboard' },
  { path: '/issues', label: 'Issues', icon: 'assignment' },
  { path: '/jobs', label: 'Jobs', icon: 'terminal' },
  { path: '/cost', label: 'Cost', icon: 'payments' },
];
