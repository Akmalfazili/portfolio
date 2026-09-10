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
          useValue: {
            title: 'Delete transaction?',
            message: 'This cannot be undone.',
            destructive,
          },
        },
        { provide: MatDialogRef, useValue: dialogRef },
      ],
    });
    fixture = TestBed.createComponent(ConfirmDialog);
    fixture.detectChanges();
  }

  // D22: Angular JIT-compiles a component's template lazily, on the *first*
  // `TestBed.createComponent()` call anywhere for that class, and caches the
  // result from then on. Paying for that one-time compile (of this dialog
  // plus `provideNoopAnimations()`'s overlay machinery) inside the first real
  // `it()` put a fixed, small cost inside the same 5s budget as everything
  // else that test does — fine in isolation, but squeezed thin under real
  // CPU contention from the rest of the suite running concurrently (measured
  // timing out intermittently). Paying it once here instead, under Vitest's
  // separate (and longer) hook timeout, means every `it()` below starts from
  // an already-compiled component and never has to absorb that variance.
  beforeAll(() => {
    setup();
    // TestBed forbids a second `configureTestingModule()` once instantiated,
    // and nothing resets it between `beforeAll` and the first real `it()` —
    // that reset normally happens in TestBed's own `afterEach`, which hasn't
    // run yet. Reset explicitly so `setup()` inside the first test can
    // configure a fresh module of its own.
    TestBed.resetTestingModule();
  });

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
