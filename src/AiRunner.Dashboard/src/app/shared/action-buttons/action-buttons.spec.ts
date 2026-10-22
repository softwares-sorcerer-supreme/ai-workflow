import { HarnessLoader } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { MatInputHarness } from '@angular/material/input/testing';
import { MatSelectHarness } from '@angular/material/select/testing';
import { MatSnackBarHarness } from '@angular/material/snack-bar/testing';
import { Api } from '../../api/api';
import { getModelOptions } from '../../api/fn/issues/get-model-options';
import { runIssueAction } from '../../api/fn/issues/run-issue-action';
import { runPullAction } from '../../api/fn/pulls/run-pull-action';
import { ActionResponse } from '../../api/models/action-response';
import { CommandResponse } from '../../api/models/command-response';
import { FakeApi, settle } from '../testing';
import { ActionButtons, ActionTarget } from './action-buttons';

const IMPLEMENT: ActionResponse = { code: 'implement', label: 'Implement', needsText: false };
const RETRY: ActionResponse = { code: 'retry', label: 'Retry', needsText: false };
const ANSWER: ActionResponse = { code: 'answer', label: 'Answer the questions', needsText: true };
const SET_MODEL: ActionResponse = { code: 'set-model', label: 'Set model', needsText: false, needsModel: true };
const WORKFLOWS = [
  { code: 'analyze', label: 'analyze' },
  { code: 'implement', label: 'implement' },
];

@Component({
  imports: [ActionButtons],
  template: `<app-action-buttons [actions]="actions()" [target]="target()" (after)="afters = afters + 1" />`,
})
class Host {
  readonly actions = signal<readonly ActionResponse[]>([]);
  readonly target = signal<ActionTarget>({ kind: 'issue', repo: 'team/shop', number: 42 });
  afters = 0;
}

describe('ActionButtons', () => {
  let api: FakeApi;

  async function render(actions: readonly ActionResponse[], target?: ActionTarget) {
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        { provide: Api, useValue: api },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });

    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.actions.set(actions);
    if (target) {
      fixture.componentInstance.target.set(target);
    }
    await settle(fixture);

    return {
      fixture,
      element: fixture.nativeElement as HTMLElement,
      loader: TestbedHarnessEnvironment.loader(fixture),
      root: TestbedHarnessEnvironment.documentRootLoader(fixture),
    };
  }

  function button(loader: HarnessLoader, code: string): Promise<MatButtonHarness> {
    return loader.getHarness(MatButtonHarness.with({ selector: `[data-action="${code}"]` }));
  }

  async function snackText(root: HarnessLoader): Promise<string> {
    const snackBar = await root.getHarness(MatSnackBarHarness);

    return snackBar.getMessage();
  }

  /** A reply that stays open until the test calls `resolve`. */
  function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((done) => (resolve = done));

    return { promise, resolve };
  }


  beforeEach(() => {
    api = new FakeApi();
  });

  it('shows one button per action, in the given order, with the given labels', async () => {
    const { element } = await render([RETRY, IMPLEMENT, ANSWER]);

    const labels = [...element.querySelectorAll('button')].map((item) => item.textContent?.trim());
    expect(labels).toEqual(['Retry', 'Implement', 'Answer the questions']);
  });

  it('shows no button when there is no action', async () => {
    const { element } = await render([]);

    expect(element.querySelectorAll('button').length).toBe(0);
  });

  it('runs an action without text at once, busy while it runs, then shows the message and calls after', async () => {
    const reply = deferred<CommandResponse>();
    api.answer(runIssueAction, () => reply.promise);
    const { fixture, loader, root } = await render([IMPLEMENT, RETRY]);

    const implement = await button(loader, 'implement');
    await implement.click();

    expect(api.callsOf(runIssueAction)).toEqual([
      { owner: 'team', repo: 'shop', number: 42, code: 'implement', body: { text: null } },
    ]);
    expect(await implement.isDisabled()).toBe(true);
    expect(await (await button(loader, 'retry')).isDisabled()).toBe(true);
    const spinner = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-action="implement"] mat-progress-spinner',
    );
    expect(spinner).not.toBeNull();

    reply.resolve({ message: 'Added ai-implement to #42.' });
    await settle(fixture);

    expect(await implement.isDisabled()).toBe(false);
    expect(await snackText(root)).toBe('Added ai-implement to #42.');
    expect(fixture.componentInstance.afters).toBe(1);
  });

  it('shows the server message of a refusal (409) and enables the button again', async () => {
    api.answer(runIssueAction, () => {
      throw new HttpErrorResponse({ status: 409, error: { message: 'Implement is not offered any more.' } });
    });
    const { fixture, loader, root } = await render([IMPLEMENT]);

    const implement = await button(loader, 'implement');
    await implement.click();
    await settle(fixture);

    expect(await snackText(root)).toBe('Implement is not offered any more.');
    expect(await implement.isDisabled()).toBe(false);
    expect(fixture.componentInstance.afters).toBe(1);
  });

  it('runs a PR action through the pull route', async () => {
    const review: ActionResponse = { code: 'review-full', label: 'Full review', needsText: false };
    api.answer(runPullAction, () => ({ message: 'A full review is asked for.' }));
    const { fixture, loader, root } = await render([review], { kind: 'pull', repo: 'team/web', number: 50 });

    await (await button(loader, 'review-full')).click();
    await settle(fixture);

    expect(api.callsOf(runPullAction)).toEqual([
      { owner: 'team', repo: 'web', number: 50, code: 'review-full', body: { text: null } },
    ]);
    expect(api.callsOf(runIssueAction)).toEqual([]);
    expect(await snackText(root)).toBe('A full review is asked for.');
  });

  it('opens a text box for an action that needs text; Send stays disabled while the text is blank', async () => {
    const { loader, root } = await render([ANSWER]);

    await (await button(loader, 'answer')).click();
    const dialog = await root.getHarness(MatDialogHarness);

    expect(await dialog.getTitleText()).toBe('Answer the questions');
    expect(await dialog.getContentText()).toContain('team/shop #42');

    const send = await dialog.getHarness(MatButtonHarness.with({ text: 'Send' }));
    expect(await send.isDisabled()).toBe(true);

    const input = await dialog.getHarness(MatInputHarness);
    await input.setValue('   ');
    expect(await send.isDisabled()).toBe(true);

    await input.setValue('Use the EU date format.');
    expect(await send.isDisabled()).toBe(false);
    expect(api.callsOf(runIssueAction)).toEqual([]);
  });

  it('sends the typed text, closes the text box and calls after', async () => {
    api.answer(runIssueAction, () => ({ message: 'Your answer was posted on #42.' }));
    const { fixture, loader, root } = await render([ANSWER]);

    await (await button(loader, 'answer')).click();
    const dialog = await root.getHarness(MatDialogHarness);
    const input = await dialog.getHarness(MatInputHarness);
    await input.setValue('Q1: yes.\nQ2: only admins.');
    await (await dialog.getHarness(MatButtonHarness.with({ text: 'Send' }))).click();
    await settle(fixture);

    expect(api.callsOf(runIssueAction)).toEqual([
      { owner: 'team', repo: 'shop', number: 42, code: 'answer', body: { text: 'Q1: yes.\nQ2: only admins.' } },
    ]);
    expect(await snackText(root)).toBe('Your answer was posted on #42.');
    expect(await root.getAllHarnesses(MatDialogHarness)).toEqual([]);
    expect(fixture.componentInstance.afters).toBe(1);
  });

  it('keeps the text box and its text open when the runner refuses the text', async () => {
    api.answer(runIssueAction, () => {
      throw new HttpErrorResponse({ status: 400, error: { message: 'This action needs a text.' } });
    });
    const { fixture, loader, root } = await render([ANSWER]);

    await (await button(loader, 'answer')).click();
    const dialog = await root.getHarness(MatDialogHarness);
    const input = await dialog.getHarness(MatInputHarness);
    await input.setValue('Q1: yes.');
    const send = await dialog.getHarness(MatButtonHarness.with({ text: 'Send' }));
    await send.click();
    await settle(fixture);

    expect(await snackText(root)).toBe('This action needs a text.');
    expect((await root.getAllHarnesses(MatDialogHarness)).length).toBe(1);
    expect(await input.getValue()).toBe('Q1: yes.');
    expect(await send.isDisabled()).toBe(false);
  });

  it('sends nothing when the text box is cancelled', async () => {
    const { fixture, loader, root } = await render([ANSWER]);

    await (await button(loader, 'answer')).click();
    const dialog = await root.getHarness(MatDialogHarness);
    await (await dialog.getHarness(MatButtonHarness.with({ text: 'Cancel' }))).click();
    await settle(fixture);

    expect(await root.getAllHarnesses(MatDialogHarness)).toEqual([]);
    expect(api.calls).toEqual([]);
    expect(fixture.componentInstance.afters).toBe(0);
  });

  describe('set-model', () => {
    it('picks a workflow and a model from allowed_models and sends both', async () => {
      api
        .answer(getModelOptions, () => ({ workflows: WORKFLOWS, allowedModels: ['claude-opus-5.5', 'claude-sonnet-5.5'] }))
        .answer(runIssueAction, () => ({ message: 'Model setting saved on team/shop#42. It applies to the next job.' }));
      const { fixture, loader, root } = await render([SET_MODEL]);

      await (await button(loader, 'set-model')).click();
      await settle(fixture);
      const dialog = await root.getHarness(MatDialogHarness);
      expect(await dialog.getTitleText()).toBe('Set model');
      expect(await dialog.getContentText()).toContain('team/shop #42');

      const save = await dialog.getHarness(MatButtonHarness.with({ text: 'Save' }));
      expect(await save.isDisabled()).toBe(true);

      const [workflow, model] = await dialog.getAllHarnesses(MatSelectHarness);
      await workflow.open();
      await workflow.clickOptions({ text: 'implement' });
      await model.open();
      expect((await model.getOptions()).length).toBe(3);
      await model.clickOptions({ text: 'claude-opus-5.5' });
      expect(await save.isDisabled()).toBe(false);

      await save.click();
      await settle(fixture);

      expect(api.callsOf(runIssueAction)).toEqual([
        {
          owner: 'team',
          repo: 'shop',
          number: 42,
          code: 'set-model',
          body: { text: null, workflow: 'implement', model: 'claude-opus-5.5' },
        },
      ]);
      expect(await snackText(root)).toBe('Model setting saved on team/shop#42. It applies to the next job.');
      expect(await root.getAllHarnesses(MatDialogHarness)).toEqual([]);
      expect(fixture.componentInstance.afters).toBe(1);
    });

    it('takes a typed model when there is no allowed list; empty removes the override', async () => {
      api
        .answer(getModelOptions, () => ({ workflows: WORKFLOWS, allowedModels: [] }))
        .answer(runIssueAction, () => ({ message: 'Model setting saved.' }));
      const { fixture, loader, root } = await render([SET_MODEL]);

      await (await button(loader, 'set-model')).click();
      await settle(fixture);
      const dialog = await root.getHarness(MatDialogHarness);

      const selects = await dialog.getAllHarnesses(MatSelectHarness);
      expect(selects.length).toBe(1);
      await selects[0].open();
      await selects[0].clickOptions({ text: 'analyze' });
      const input = await dialog.getHarness(MatInputHarness);
      expect(await input.getValue()).toBe('');

      await (await dialog.getHarness(MatButtonHarness.with({ text: 'Save' }))).click();
      await settle(fixture);

      expect(api.callsOf(runIssueAction)).toEqual([
        { owner: 'team', repo: 'shop', number: 42, code: 'set-model', body: { text: null, workflow: 'analyze', model: '' } },
      ]);
    });

    it('keeps the picker open with the runner message on a refusal', async () => {
      api
        .answer(getModelOptions, () => ({ workflows: WORKFLOWS, allowedModels: [] }))
        .answer(runIssueAction, () => {
          throw new HttpErrorResponse({ status: 400, error: { message: 'gpt-x is not in allowed_models.' } });
        });
      const { fixture, loader, root } = await render([SET_MODEL]);

      await (await button(loader, 'set-model')).click();
      await settle(fixture);
      const dialog = await root.getHarness(MatDialogHarness);
      const select = await dialog.getHarness(MatSelectHarness);
      await select.open();
      await select.clickOptions({ text: 'implement' });
      await (await dialog.getHarness(MatInputHarness)).setValue('gpt-x');
      await (await dialog.getHarness(MatButtonHarness.with({ text: 'Save' }))).click();
      await settle(fixture);

      expect(await snackText(root)).toBe('gpt-x is not in allowed_models.');
      expect((await root.getAllHarnesses(MatDialogHarness)).length).toBe(1);
    });

    it('shows an error with Try again when the choices cannot be read, and sends nothing', async () => {
      api.answer(getModelOptions, () => {
        throw new HttpErrorResponse({ status: 500, error: { message: 'The runner failed.' } });
      });
      const { fixture, loader, root } = await render([SET_MODEL]);

      await (await button(loader, 'set-model')).click();
      await settle(fixture);
      const dialog = await root.getHarness(MatDialogHarness);

      expect(await dialog.getContentText()).toContain('Could not load the models');
      expect(await dialog.getContentText()).toContain('The runner failed.');
      expect(await (await dialog.getHarness(MatButtonHarness.with({ text: 'Save' }))).isDisabled()).toBe(true);
      expect(api.callsOf(runIssueAction)).toEqual([]);
    });
  });
});
