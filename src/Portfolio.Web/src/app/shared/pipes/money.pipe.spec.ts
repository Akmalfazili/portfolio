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

  describe("'price' mode — a per-unit rate, not a total", () => {
    it('renders a per-share rate at its own real precision above one cent, where total mode would round it away', () => {
      // The defect this mode exists to fix: two Z74 dividend rows both read
      // "SGD 0.10" in total mode (0.103 and 0.100 both round to 2dp), making
      // the income column impossible to reconcile against the rate column.
      expect(pipe.transform(0.103, 'SGD', 'price')).toBe('SGD 0.103');
      expect(pipe.transform(0.1, 'SGD', 'price')).toBe('SGD 0.10');
      expect(pipe.transform(0.082, 'SGD', 'price')).toBe('SGD 0.082');
    });

    it('never pads a clean value out to 10dp trailing-zero noise, but keeps the 2dp floor', () => {
      expect(pipe.transform(0.1, 'USD', 'price')).toBe('$0.10');
      expect(pipe.transform(2.5, 'USD', 'price')).toBe('$2.50');
      expect(pipe.transform(2, 'USD', 'price')).toBe('$2.00');
    });

    it('still never floors a sub-cent price to $0.00, same as total mode', () => {
      expect(pipe.transform(0.0005326, 'USD', 'price')).toBe('$0.0005326');
    });

    it('leaves total mode (the default, and the explicit "total") unchanged — a genuine monetary total still renders at 2dp', () => {
      expect(pipe.transform(0.103)).toBe('$0.10');
      expect(pipe.transform(0.103, 'USD', 'total')).toBe('$0.10');
    });
  });
});
