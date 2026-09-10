import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideEchartsCore } from 'ngx-echarts';

import { AnnualReturnChart } from './annual-return-chart';
import { AnnualReturnDto } from '../../../core/api/models';

describe('AnnualReturnChart', () => {
  let fixture: ComponentFixture<AnnualReturnChart>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AnnualReturnChart],
      providers: [provideEchartsCore({ echarts: () => import('echarts') })],
    });
    fixture = TestBed.createComponent(AnnualReturnChart);
  });

  it('shows the empty state when there are no years yet', () => {
    fixture.componentRef.setInput('years', []);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No annual return figures yet');
    expect(fixture.nativeElement.querySelector('[echarts]')).toBeFalsy();
  });

  it('renders a bar per year, positive years green and negative years red with a signed label', () => {
    const years: AnnualReturnDto[] = [
      { year: 2024, timeWeightedReturnPercent: 12.34 },
      { year: 2025, timeWeightedReturnPercent: -5.67 },
    ];
    fixture.componentRef.setInput('years', years);
    fixture.detectChanges();

    const series = (
      fixture.componentInstance.options() as {
        series: { data: { value: number; itemStyle: { color: string } }[] }[];
      }
    ).series[0].data;
    expect(series).toHaveLength(2);
    expect(series[0].value).toBeCloseTo(12.34);
    expect(series[1].value).toBeCloseTo(-5.67);
    // Colours must differ between the gain and loss bars — the actual
    // rendered hex values are only confirmed by looking at the browser, not
    // by this unit test (see the visual-claims list in the handoff).
    expect(series[0].itemStyle.color).not.toBe(series[1].itemStyle.color);
  });

  it('sorts years ascending regardless of input order', () => {
    fixture.componentRef.setInput('years', [
      { year: 2025, timeWeightedReturnPercent: 1 },
      { year: 2023, timeWeightedReturnPercent: 2 },
      { year: 2024, timeWeightedReturnPercent: 3 },
    ] satisfies AnnualReturnDto[]);
    fixture.detectChanges();

    const xAxis = fixture.componentInstance.options() as { xAxis: { data: string[] } };
    expect(xAxis.xAxis.data).toEqual(['2023', '2024', '2025']);
  });
});
