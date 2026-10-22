import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { Api } from '../../api/api';
import { getSetup } from '../../api/fn/setup/get-setup';
import { removeToken } from '../../api/fn/setup/remove-token';
import { removeWebPassword } from '../../api/fn/setup/remove-web-password';
import { saveToken } from '../../api/fn/setup/save-token';
import { saveWebPassword } from '../../api/fn/setup/save-web-password';
import { HealthResponse } from '../../api/models/health-response';
import { SetupResponse } from '../../api/models/setup-response';
import { HealthStore } from '../../layout/health-store';
import {
  FakeApi,
  FakePollResource,
  fakePollResource,
  sampleHealth,
  sampleSetup,
  settle,
} from '../../shared/testing';
import { Setup } from './setup';

/** No token yet: the state the runner starts in on a new machine. */
const NO_TOKEN = sampleSetup({
  isTokenSet: false,
  tokenSource: 'none',
  tokenSourceLabel: 'Not set',
  login: '',
  canRemoveToken: false,
});

describe('Setup', () => {
  let api: FakeApi;
  let health: FakePollResource<HealthResponse>;

  /** Answers the first GET /api/setup with `first`; later polls never answer, so a write's answer stays shown. */
  function answerSetupOnce(first: SetupResponse): void {
    let calls = 0;
    api.answer(getSetup, () => {
      calls++;

      return calls === 1 ? first : new Promise<SetupResponse>(() => undefined);
    });
  }

  async function render() {
    await TestBed.configureTestingModule({
      imports: [Setup],
      providers: [
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
        { provide: HealthStore, useValue: { health } },
        { provide: Api, useValue: api },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(Setup);
    await settle(fixture);

    return {
      fixture,
      element: fixture.nativeElement as HTMLElement,
      loader: TestbedHarnessEnvironment.loader(fixture),
      root: TestbedHarnessEnvironment.documentRootLoader(fixture),
    };
  }

  function text(element: HTMLElement, testId: string): string | undefined {
    return element.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim();
  }

  function input(loader: HarnessLoader, testId: string): Promise<MatInputHarness> {
    return loader.getHarness(MatInputHarness.with({ selector: `[data-testid="${testId}"]` }));
  }

  function button(loader: HarnessLoader, testId: string): Promise<MatButtonHarness> {
    return loader.getHarness(MatButtonHarness.with({ selector: `[data-testid="${testId}"]` }));
  }

  async function snackText(root: HarnessLoader): Promise<string> {
    const snackBar = await root.getHarness(MatSnackBarHarness);

    return snackBar.getMessage();
  }

  async function choose(root: HarnessLoader, label: string): Promise<void> {
    const dialog = await root.getHarness(MatDialogHarness);
    const choice = await dialog.getHarness(MatButtonHarness.with({ text: label }));
    await choice.click();
  }

  beforeEach(() => {
    api = new FakeApi();
    health = fakePollResource(sampleHealth());
  });

  it('shows the base URL, the secrets file, the login and where each secret comes from', async () => {
    api.answer(getSetup, () => sampleSetup());

    const { element } = await render();

    expect(text(element, 'base-url')).toBe('http://gitbucket.local');
    expect(element.textContent).toContain('code_host.base_url');
    expect(text(element, 'secrets-file')).toBe(
      '/home/dev/.microsoft/usersecrets/ai-runner/secrets.json',
    );
    expect(text(element, 'token-source')).toBe('User secrets');
    expect(text(element, 'login')).toBe('ai-bot');
    expect(text(element, 'web-password-source')).toBe('Not set');
    expect(element.textContent).toContain('cannot close an issue');
  });

  it('says a token from the environment is changed there and offers no Remove', async () => {
    api.answer(getSetup, () =>
      sampleSetup({
        tokenSource: 'environment',
        tokenSourceLabel: 'Environment variable',
        tokenNote: 'This token comes from the GITBUCKET_TOKEN environment variable; change it there.',
        canRemoveToken: false,
        webPasswordSource: 'repos-yml',
        webPasswordSourceLabel: 'repos.yml',
        webPasswordNote: 'This password comes from code_host.web_password in repos.yml; change it there.',
      }),
    );

    const { element } = await render();
    const tokenCard = element.querySelector('[data-testid="token-card"]') as HTMLElement;
    const webPasswordCard = element.querySelector(
      '[data-testid="web-password-card"]',
    ) as HTMLElement;

    expect(text(element, 'token-source')).toBe('Environment variable');
    expect(tokenCard.querySelector('app-inline-note')?.textContent).toContain('GITBUCKET_TOKEN');
    expect(text(element, 'web-password-source')).toBe('repos.yml');
    expect(webPasswordCard.querySelector('app-inline-note')?.textContent).toContain('repos.yml');
    expect(element.querySelector('[data-testid="token-remove"]')).toBeNull();
    expect(element.querySelector('[data-testid="web-password-remove"]')).toBeNull();
  });

  it('shows no login and no Remove while no token is set', async () => {
    api.answer(getSetup, () => NO_TOKEN);

    const { element } = await render();

    expect(text(element, 'token-source')).toBe('Not set');
    expect(element.querySelector('[data-testid="login"]')).toBeNull();
    expect(element.querySelector('[data-testid="token-remove"]')).toBeNull();
  });

  it('starts with empty password fields and Check and save disabled until something is typed', async () => {
    api.answer(getSetup, () =>
      sampleSetup({ isWebPasswordSet: true, webPasswordSource: 'user-secrets' }),
    );
    const { loader } = await render();

    const tokenField = await input(loader, 'token-input');
    const passwordField = await input(loader, 'web-password-input');
    const saveButton = await button(loader, 'token-save');

    expect(await tokenField.getType()).toBe('password');
    expect(await tokenField.getValue()).toBe('');
    expect(await passwordField.getType()).toBe('password');
    expect(await passwordField.getValue()).toBe('');
    expect(await saveButton.isDisabled()).toBe(true);

    await tokenField.setValue('secret');

    expect(await saveButton.isDisabled()).toBe(false);
  });

  it('checks and saves a token, clears the field, shows the message and the setup the runner returned', async () => {
    answerSetupOnce(NO_TOKEN);
    api.answer(saveToken, () => ({
      message: 'Token saved. Signed in as ai-bot.',
      setup: sampleSetup({ login: 'ai-bot' }),
    }));
    const { fixture, element, loader, root } = await render();

    const tokenField = await input(loader, 'token-input');
    await tokenField.setValue('new-token');
    const saveButton = await button(loader, 'token-save');
    await saveButton.click();
    await settle(fixture);

    expect(api.callsOf(saveToken)).toEqual([{ body: { token: 'new-token' } }]);
    expect(await tokenField.getValue()).toBe('');
    expect(await snackText(root)).toBe('Token saved. Signed in as ai-bot.');
    expect(text(element, 'token-source')).toBe('User secrets');
    expect(text(element, 'login')).toBe('ai-bot');
    expect(health.reloads).toBe(1);
  });

  it('shows the message of a refused token, clears the field and keeps the runner state as it was', async () => {
    answerSetupOnce(NO_TOKEN);
    api.answer(saveToken, () => {
      throw new HttpErrorResponse({
        status: 400,
        error: { message: 'GitBucket at http://gitbucket.local is not reachable (VPN?)' },
      });
    });
    const { fixture, element, loader, root } = await render();

    const tokenField = await input(loader, 'token-input');
    await tokenField.setValue('new-token');
    const saveButton = await button(loader, 'token-save');
    await saveButton.click();
    await settle(fixture);

    expect(await snackText(root)).toBe(
      'GitBucket at http://gitbucket.local is not reachable (VPN?)',
    );
    expect(await tokenField.getValue()).toBe('');
    expect(text(element, 'token-source')).toBe('Not set');
    expect(element.querySelector('[data-testid="login"]')).toBeNull();
  });

  it('checks and saves the web password', async () => {
    answerSetupOnce(sampleSetup());
    api.answer(saveWebPassword, () => ({
      message: 'Web password saved.',
      setup: sampleSetup({
        isWebPasswordSet: true,
        webPasswordSource: 'user-secrets',
        webPasswordSourceLabel: 'User secrets',
        canRemoveWebPassword: true,
      }),
    }));
    const { fixture, element, loader, root } = await render();

    const passwordField = await input(loader, 'web-password-input');
    await passwordField.setValue('hunter2');
    const saveButton = await button(loader, 'web-password-save');
    await saveButton.click();
    await settle(fixture);

    expect(api.callsOf(saveWebPassword)).toEqual([{ body: { password: 'hunter2' } }]);
    expect(await passwordField.getValue()).toBe('');
    expect(await snackText(root)).toBe('Web password saved.');
    expect(text(element, 'web-password-source')).toBe('User secrets');
    expect(element.querySelector('[data-testid="web-password-remove"]')).not.toBeNull();
  });

  it('asks before it removes the token; "Keep token" makes no call', async () => {
    api.answer(getSetup, () => sampleSetup());
    const { fixture, loader, root } = await render();

    const removeButton = await button(loader, 'token-remove');
    await removeButton.click();
    const dialog = await root.getHarness(MatDialogHarness);

    expect(await dialog.getRole()).toBe('alertdialog');
    expect(await dialog.getTitleText()).toBe('Remove the saved token?');

    await choose(root, 'Keep token');
    await settle(fixture);

    expect(api.callsOf(removeToken)).toEqual([]);
  });

  it('removes the token after the confirmation and shows the setup the runner returned', async () => {
    answerSetupOnce(sampleSetup());
    api.answer(removeToken, () => ({ message: 'Token removed.', setup: NO_TOKEN }));
    const { fixture, element, loader, root } = await render();

    const removeButton = await button(loader, 'token-remove');
    await removeButton.click();
    await choose(root, 'Remove token');
    await settle(fixture);

    expect(api.callsOf(removeToken).length).toBe(1);
    expect(await snackText(root)).toBe('Token removed.');
    expect(text(element, 'token-source')).toBe('Not set');
    expect(element.querySelector('[data-testid="token-remove"]')).toBeNull();
  });

  it('removes the web password after the confirmation, and shows a 409 as sent', async () => {
    answerSetupOnce(
      sampleSetup({
        isWebPasswordSet: true,
        webPasswordSource: 'user-secrets',
        canRemoveWebPassword: true,
      }),
    );
    api.answer(removeWebPassword, () => {
      throw new HttpErrorResponse({
        status: 409,
        error: { message: 'No web password is saved in the user secrets.' },
      });
    });
    const { fixture, loader, root } = await render();

    const removeButton = await button(loader, 'web-password-remove');
    await removeButton.click();
    const dialog = await root.getHarness(MatDialogHarness);
    expect(await dialog.getTitleText()).toBe('Remove the saved web password?');
    await choose(root, 'Remove password');
    await settle(fixture);

    expect(api.callsOf(removeWebPassword).length).toBe(1);
    expect(await snackText(root)).toBe('No web password is saved in the user secrets.');
  });

  it('shows an error with a retry when the setup cannot be read', async () => {
    api.answer(getSetup, () => {
      throw new HttpErrorResponse({ status: 0 });
    });

    const { element } = await render();

    expect(element.textContent).toContain('Could not load the setup');
    expect(element.textContent).toContain('The runner is not reachable.');
    expect(element.querySelector('[data-testid="token-input"]')).toBeNull();
  });
});
