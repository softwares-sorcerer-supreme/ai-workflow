import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { MatIconRegistry } from '@angular/material/icon';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideApiConfiguration } from './api/api-configuration';
import { routes } from './app.routes';
import { writeGuardInterceptor } from './shared/write-guard.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // Every write carries the runner's CSRF guard header (see the interceptor); GET requests are left alone.
    provideHttpClient(withFetch(), withInterceptors([writeGuardInterceptor])),
    // The API is served by the same origin (the runner, or the dev-server proxy).
    provideApiConfiguration(''),
    // Icons come from the bundled Material Symbols font, never from a CDN (the runner may be offline).
    provideAppInitializer(() => {
      inject(MatIconRegistry).setDefaultFontSetClass('material-symbols-outlined');
    }),
  ],
};
