import { MoneyPipe } from './money.pipe';

describe('MoneyPipe', () => {
  const pipe = new MoneyPipe();

  it('never floors a sub-cent ANVL-scale price to $0.00', () => {
    expect(pipe.transform(0.0005326)).toBe('$0.0005326');
    expect(pipe.transform(0.0005326)).not.toBe('$0.00');
  });

  it('renders another sub-cent value without trailing-zero noise', () => {
    expect(pipe.transform(0.00042369)).toBe('$0.00042369');
  });

  it('renders a whole-dollar quantity used as a price with 2dp', () => {
    expect(pipe.transform(1000000)).toBe('$1,000,000.00');
  });

  it('renders a normal equity price to the conventional 2dp', () => {
    expect(pipe.transform(333.019989)).toBe('$333.02');
  });

  it('renders exact zero as $0.00', () => {
    expect(pipe.transform(0)).toBe('$0.00');
  });

  it('renders null/undefined/blank as an em dash rather than $0.00 or NaN', () => {
    expect(pipe.transform(null)).toBe('—');
    expect(pipe.transform(undefined)).toBe('—');
    expect(pipe.transform('')).toBe('—');
  });

  it('accepts numeric strings, as values come back from JSON', () => {
    expect(pipe.transform('0.0005326')).toBe('$0.0005326');
  });

  it('respects an explicit currency code without doing FX math', () => {
    // Intl.NumberFormat separates the ISO code from the amount with a non-breaking space (U+00A0).
    expect(pipe.transform(4.39, 'SGD')).toBe('SGD 4.39');
  });
});
