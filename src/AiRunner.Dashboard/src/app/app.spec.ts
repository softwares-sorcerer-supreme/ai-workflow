import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { HealthStore } from './layout/health-store';
import { fakePollResource, fixedClock, sampleHealth } from './shared/testing';

describe('App shell', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        fixedClock,
        { provide: HealthStore, useValue: { health: fakePollResource(sampleHealth()) } },
      ],
    }).compileComponents();
  });

  it('has the health strip and one menu entry per screen', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    const entries = [...element.querySelectorAll('mat-nav-list a')].map((link) => link.textContent?.trim());

    expect(element.querySelector('app-health-strip')).not.toBeNull();
    expect(entries).toEqual(['dashboardOverview', 'assignmentIssues', 'terminalJobs', 'paymentsCost', 'keySetup']);
  });
});
