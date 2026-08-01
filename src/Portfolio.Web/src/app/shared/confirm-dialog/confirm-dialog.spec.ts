import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';

import { ConfirmDialog } from './confirm-dialog';

describe('ConfirmDialog', () => {
  let fixture: ComponentFixture<ConfirmDialog>;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  function setup(destructive = false) {
    dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [ConfirmDialog],
      providers: [
        provideNoopAnimations(),
        {
          provide: MAT_DIALOG_DATA,
          useValue: { title: 'Delete transaction?', message: 'This cannot be undone.', destructive },
        },
        { provide: MatDialogRef, useValue: dialogRef },
      ],
    });
    fixture = TestBed.createComponent(ConfirmDialog);
    fixture.detectChanges();
  }

  it('closes with true on confirm', () => {
    setup();
    fixture.componentInstance.confirm();
    expect(dialogRef.close).toHaveBeenCalledWith(true);
  });

  it('closes with false on cancel', () => {
    setup();
    fixture.componentInstance.cancel();
    expect(dialogRef.close).toHaveBeenCalledWith(false);
  });

  it('renders the provided title and message', () => {
    setup();
    expect(fixture.nativeElement.textContent).toContain('Delete transaction?');
    expect(fixture.nativeElement.textContent).toContain('This cannot be undone.');
  });
});
