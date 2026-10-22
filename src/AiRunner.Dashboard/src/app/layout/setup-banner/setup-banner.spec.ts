import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { HealthResponse } from '../../api/models/health-response';
import { FakePollResource, fakePollResource, sampleHealth, settle } from '../../shared/testing';
import { HealthStore } from '../health-store';
import { SetupBanner } from './setup-banner';

@Component({ template: '' })
class EmptyPage {}

describe('SetupBanner', () => {
  let health: FakePollResource<HealthResponse>;

  async function renderAt(url: string) {
    await TestBed.configureTestingModule({
      imports: [SetupBanner],
      providers: [
        provideRouter([
          { path: 'overview', component: EmptyPage },
          { path: 'setup', component: EmptyPage },
        ]),
        { provide: HealthStore, useValue: { health } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SetupBanner);
    await TestBed.inject(Router).navigateByUrl(url);
    await settle(fixture);

    return fixture;
  }

  function banner(fixture: { nativeElement: unknown }): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector('[data-testid="setup-banner"]');
  }

  it('says nothing runs and links to Setup while setup is needed', async () => {
    health = fakePollResource(sampleHealth({ state: 'needs-setup', isSetupNeeded: true }));

    const fixture = await renderAt('/overview');

    expect(banner(fixture)?.textContent).toContain('no GitBucket token yet');
    expect(banner(fixture)?.textContent).toContain('polls nothing and runs no job');
    expect(banner(fixture)?.querySelector('a')?.getAttribute('href')).toBe('/setup');
  });

  it('is hidden on the Setup screen itself', async () => {
    health = fakePollResource(sampleHealth({ state: 'needs-setup', isSetupNeeded: true }));

    const fixture = await renderAt('/setup');

    expect(banner(fixture)).toBeNull();
  });

  it('is hidden once the runner has a token, and before the first health answer', async () => {
    health = fakePollResource(sampleHealth());
    const fixture = await renderAt('/overview');

    expect(banner(fixture)).toBeNull();

    health.value.set(undefined);
    await settle(fixture);

    expect(banner(fixture)).toBeNull();
  });

  it('appears and goes away with the health the shell polls', async () => {
    health = fakePollResource(sampleHealth({ isSetupNeeded: true }));
    const fixture = await renderAt('/overview');

    expect(banner(fixture)).not.toBeNull();

    health.value.set(sampleHealth({ isSetupNeeded: false }));
    await settle(fixture);

    expect(banner(fixture)).toBeNull();
  });
});
