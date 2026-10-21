import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatButtonHarness } from '@angular/material/button/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { MatDialog } from '@angular/material/dialog';
import { MatDialogHarness } from '@angular/material/dialog/testing';
import { ConfirmDialogData, confirmWrite } from './confirm-dialog';

@Component({ template: '' })
class Host {
  readonly dialog = inject(MatDialog);
}

const DATA: ConfirmDialogData = {
  title: 'Cancel the running job?',
  message: 'The job stops now.',
  confirmLabel: 'Cancel job',
  dismissLabel: 'Keep running',
};

describe('confirmWrite', () => {
  async function open() {
    TestBed.configureTestingModule({
      providers: [{ provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } }],
    });

    const fixture = TestBed.createComponent(Host);
    const answer = confirmWrite(fixture.componentInstance.dialog, DATA);
    const root = TestbedHarnessEnvironment.documentRootLoader(fixture);
    const dialog = await root.getHarness(MatDialogHarness);

    return { answer, dialog };
  }

  it('shows the texts as an alert dialog', async () => {
    const { dialog } = await open();

    expect(await dialog.getRole()).toBe('alertdialog');
    expect(await dialog.getTitleText()).toBe('Cancel the running job?');
    expect(await dialog.getContentText()).toBe('The job stops now.');
    expect(await dialog.getActionsText()).toContain('Keep running');
  });

  it('resolves to true on the confirm button', async () => {
    const { answer, dialog } = await open();

    const confirm = await dialog.getHarness(MatButtonHarness.with({ text: 'Cancel job' }));
    await confirm.click();

    expect(await answer).toBe(true);
  });

  it('resolves to false on the dismiss button', async () => {
    const { answer, dialog } = await open();

    const dismiss = await dialog.getHarness(MatButtonHarness.with({ text: 'Keep running' }));
    await dismiss.click();

    expect(await answer).toBe(false);
  });

  it('resolves to false when closed without a choice (Escape)', async () => {
    const { answer, dialog } = await open();

    await dialog.close();

    expect(await answer).toBe(false);
  });
});
