import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { StatTile } from './stat-tile';

@Component({
  standalone: true,
  imports: [StatTile],
  template: `
    <app-stat-tile label="Cost basis" value="$3,864.00">
      <span class="projected">delta content</span>
    </app-stat-tile>
  `,
})
class HostComponent {}

describe('StatTile', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  it('renders the label and value', () => {
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Cost basis');
    expect(text).toContain('$3,864.00');
  });

  it('projects delta content', () => {
    expect(fixture.nativeElement.querySelector('.projected')?.textContent).toContain('delta content');
  });
});
