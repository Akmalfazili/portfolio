import { ComponentFixture, TestBed } from '@angular/core/testing';

import { GainLoss } from './gain-loss';

describe('GainLoss', () => {
  let fixture: ComponentFixture<GainLoss>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [GainLoss] });
    fixture = TestBed.createComponent(GainLoss);
  });

  /** The `d` of the rendered arrow — the direction cue, now that it is a shape
   *  rather than a word. Compared between cases rather than asserted literally,
   *  so these tests pin the *distinction* and not a specific Material path. */
  function iconPath(): string {
    return fixture.nativeElement.querySelector('.gain-loss__icon path')?.getAttribute('d') ?? '';
  }

  it('shows a "+" sign, an up arrow and the gain colour class for a positive amount', () => {
    fixture.componentRef.setInput('amount', 27.625);
    fixture.componentRef.setInput('percent', 3.0525);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('+$27.63');
    expect(text).toContain('+3.05%');
    expect(fixture.nativeElement.querySelector('.gain-loss--gain')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.gain-loss--loss')).toBeFalsy();
    expect(iconPath()).not.toBe('');
  });

  it('shows the existing "-" sign, a down arrow and the loss colour class for a negative amount', () => {
    fixture.componentRef.setInput('amount', -3864);
    fixture.componentRef.setInput('percent', -100);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('-$3,864.00');
    expect(text).toContain('-100.00%');
    expect(fixture.nativeElement.querySelector('.gain-loss--loss')).toBeTruthy();
  });

  it('draws a visually different glyph for gain, loss and unchanged', () => {
    fixture.componentRef.setInput('amount', 10);
    fixture.detectChanges();
    const up = iconPath();

    fixture.componentRef.setInput('amount', -10);
    fixture.detectChanges();
    const down = iconPath();

    fixture.componentRef.setInput('amount', 0);
    fixture.detectChanges();
    const flat = iconPath();

    expect(new Set([up, down, flat]).size).toBe(3);
  });

  /**
   * D18 residual, and the reason this spec was rewritten rather than extended:
   * the previous version asserted `textContent` *contained* `arrow_upward`,
   * which pinned the defect in place. `aria-hidden` had already fixed the
   * accessibility tree, but the ligature name is literal text inside a
   * `<mat-icon>`, so raw text extraction still produced
   * `arrow_upward+$218.09·+40.95%`. An SVG has no text to leak.
   */
  it('leaks no icon name into raw textContent', () => {
    for (const amount of [218.09, -1.35, 0]) {
      fixture.componentRef.setInput('amount', amount);
      fixture.detectChanges();

      const text = fixture.nativeElement.textContent as string;
      expect(text).not.toContain('arrow_upward');
      expect(text).not.toContain('arrow_downward');
      expect(text).not.toContain('remove');
    }
  });

  it('reads its direction as a word to assistive tech, with the glyph itself hidden', () => {
    fixture.componentRef.setInput('amount', -1.35);
    fixture.componentRef.setInput('percent', -0.2);
    fixture.detectChanges();

    const icon = fixture.nativeElement.querySelector('.gain-loss__icon') as SVGElement;
    expect(icon.getAttribute('aria-hidden')).toBe('true');
    expect(icon.getAttribute('aria-label')).toBeNull();

    const label = fixture.nativeElement.querySelector('[role="img"]') as HTMLElement;
    expect(label.getAttribute('aria-label')).toBe('Down');

    fixture.componentRef.setInput('amount', 218.09);
    fixture.detectChanges();
    expect(label.getAttribute('aria-label')).toBe('Up');

    fixture.componentRef.setInput('amount', 0);
    fixture.componentRef.setInput('percent', null);
    fixture.detectChanges();
    expect(label.getAttribute('aria-label')).toBe('Unchanged');
  });

  it('never floors a sub-cent gain to $0.00', () => {
    fixture.componentRef.setInput('amount', 0.0004538);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('+$0.0004538');
  });

  it('renders nothing when both amount and percent are null', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.gain-loss')).toBeFalsy();
  });

  it('applies no colour class at exactly zero', () => {
    fixture.componentRef.setInput('amount', 0);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.gain-loss--gain')).toBeFalsy();
    expect(fixture.nativeElement.querySelector('.gain-loss--loss')).toBeFalsy();
  });
});
