import { Routes } from '@angular/router';

/** Page title in the browser tab: "<screen> - ai-runner". */
const titled = (screen: string) => `${screen} - ai-runner`;

// Every screen is loaded on first visit, so a heavy screen (log viewer, charts) never slows down the others.
const placeholder = () => import('./pages/placeholder/placeholder-page').then((page) => page.PlaceholderPage);

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  {
    path: 'overview',
    title: titled('Overview'),
    loadComponent: () => import('./pages/overview/overview').then((page) => page.Overview),
  },
  {
    path: 'issues',
    title: titled('Issues'),
    loadComponent: placeholder,
    data: { heading: 'Issues', subtitle: 'Every issue the runner polls, with its stage.' },
  },
  {
    path: 'issues/:owner/:repo/:number',
    title: titled('Issue'),
    loadComponent: placeholder,
    data: { heading: 'Issue', subtitle: 'One issue end to end: jobs, sub-issues and pull requests.' },
  },
  {
    path: 'jobs',
    title: titled('Jobs'),
    loadComponent: placeholder,
    data: { heading: 'Jobs', subtitle: 'Job history with outcome, duration and logs.' },
  },
  {
    path: 'cost',
    title: titled('Cost'),
    loadComponent: placeholder,
    data: { heading: 'Cost', subtitle: 'Copilot calls and job time per day, project and issue.' },
  },
  {
    path: '**',
    title: titled('Not found'),
    loadComponent: placeholder,
    data: {
      heading: 'Page not found',
      message: 'There is no screen at this address. Use the menu to pick one.',
    },
  },
];
