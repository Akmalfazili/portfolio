import { QuantityPipe } from './quantity.pipe';

describe('QuantityPipe', () => {
  const pipe = new QuantityPipe();

  it('trims a whole-number quantity to no trailing decimal noise', () => {
    expect(pipe.transform(1000000.0)).toBe('1,000,000');
  });

  it('preserves a fractional ANVL-scale quantity in full', () => {
    expect(pipe.transform(0.0005326)).toBe('0.0005326');
  });

  it('preserves another fractional quantity in full', () => {
    expect(pipe.transform(0.00042369)).toBe('0.00042369');
  });

  it('preserves a 6dp fractional share count', () => {
    expect(pipe.transform(333.019989)).toBe('333.019989');
  });

  it('caps at 10 decimal places by default, matching decimal(28,10), with no trailing zero padding', () => {
    expect(pipe.transform(1.123456789012345)).toBe('1.123456789');
  });

  it('renders null/undefined/blank as an em dash rather than 0', () => {
    expect(pipe.transform(null)).toBe('—');
    expect(pipe.transform(undefined)).toBe('—');
    expect(pipe.transform('')).toBe('—');
  });

  it('accepts numeric strings, as values come back from JSON', () => {
    expect(pipe.transform('1000000.0000000000')).toBe('1,000,000');
  });
});
