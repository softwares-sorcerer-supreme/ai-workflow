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
    loadComponent: () => import('./pages/issues/issues').then((page) => page.Issues),
  },
  {
    path: 'issues/:owner/:repo/:number',
    title: titled('Issue'),
    loadComponent: () => import('./pages/issue-detail/issue-detail').then((page) => page.IssueDetail),
  },
  {
    path: 'jobs',
    title: titled('Jobs'),
    loadComponent: () => import('./pages/jobs/jobs').then((page) => page.Jobs),
  },
  {
    path: 'jobs/:id',
    title: titled('Job'),
    loadComponent: () => import('./pages/job-log/job-log').then((page) => page.JobLog),
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
