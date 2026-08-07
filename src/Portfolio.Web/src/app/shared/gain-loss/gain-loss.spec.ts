import { ComponentFixture, TestBed } from '@angular/core/testing';

import { GainLoss } from './gain-loss';

describe('GainLoss', () => {
  let fixture: ComponentFixture<GainLoss>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [GainLoss] });
    fixture = TestBed.createComponent(GainLoss);
  });

  it('shows a "+" sign, an up arrow and the gain colour class for a positive amount', () => {
    fixture.componentRef.setInput('amountUsd', 27.625);
    fixture.componentRef.setInput('percent', 3.0525);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('+$27.63');
    expect(text).toContain('+3.05%');
    expect(fixture.nativeElement.querySelector('.gain-loss--gain')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.gain-loss--loss')).toBeFalsy();
    expect(fixture.nativeElement.textContent).toContain('arrow_upward');
  });

  it('D18 — hides the decorative icon ligature from the accessibility tree and carries the direction on a visible aria-label instead', () => {
    fixture.componentRef.setInput('amountUsd', -1.35);
    fixture.componentRef.setInput('percent', -0.2);
    fixture.detectChanges();

    const icon = fixture.nativeElement.querySelector('.gain-loss__icon') as HTMLElement;
    expect(icon.getAttribute('aria-hidden')).toBe('true');
    expect(icon.getAttribute('aria-label')).toBeNull();

    const label = fixture.nativeElement.querySelector('[role="img"]') as HTMLElement;
    expect(label.getAttribute('aria-label')).toBe('Down');
  });

  it('shows the existing "-" sign, a down arrow and the loss colour class for a negative amount', () => {
    fixture.componentRef.setInput('amountUsd', -3864);
    fixture.componentRef.setInput('percent', -100);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('-$3,864.00');
    expect(text).toContain('-100.00%');
    expect(fixture.nativeElement.querySelector('.gain-loss--loss')).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('arrow_downward');
  });

  it('never floors a sub-cent gain to $0.00', () => {
    fixture.componentRef.setInput('amountUsd', 0.0004538);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('+$0.0004538');
  });

  it('renders nothing when both amount and percent are null', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.gain-loss')).toBeFalsy();
  });

  it('renders a neutral "remove" icon with no colour class at exactly zero', () => {
    fixture.componentRef.setInput('amountUsd', 0);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.gain-loss--gain')).toBeFalsy();
    expect(fixture.nativeElement.querySelector('.gain-loss--loss')).toBeFalsy();
    expect(fixture.nativeElement.textContent).toContain('remove');
  });
});
