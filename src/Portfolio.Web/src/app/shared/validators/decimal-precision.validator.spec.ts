import { FormControl } from '@angular/forms';

import {
  decimalPrecisionValidator,
  nonNegativeNumberValidator,
  positiveNumberValidator,
} from './decimal-precision.validator';

describe('decimalPrecisionValidator', () => {
  it('accepts a 10dp crypto-dust quantity like the live D8 probe value (0.0000000001)', () => {
    const control = new FormControl<number | null>(0.0000000001);
    expect(decimalPrecisionValidator(10)(control)).toBeNull();
  });

  it('accepts exactly 10 decimal places', () => {
    const control = new FormControl<number | null>(0.1234567891);
    expect(decimalPrecisionValidator(10)(control)).toBeNull();
  });

  it('rejects more than the configured decimal places', () => {
    const control = new FormControl<number | null>(1.23456789012);
    const result = decimalPrecisionValidator(10)(control);
    expect(result).not.toBeNull();
    expect(result!['maxDecimals'] ?? result!['maxSignificantDigits']).toBeTruthy();
  });

  it('flags the D8 above-15-significant-digit case rather than letting it through silently', () => {
    // 12345678.1234567891 has already been rounded by the JS double parser to
    // 12345678.12345679 by the time it reaches a FormControl<number> — 16
    // significant digits, still over the 15-digit cap.
    const control = new FormControl<number | null>(12345678.1234567891);
    const result = decimalPrecisionValidator(10, 15)(control);
    expect(result).not.toBeNull();
    expect(result!['maxSignificantDigits']).toEqual({ max: 15, actual: 16 });
  });

  it('does not reject a value merely because JS would print it in exponential notation', () => {
    // String(0.0000000001) is "1e-10", not "0.0000000001" — a naive check for
    // "e" in that string would rejected exactly the value the live D8 probe
    // proved the backend accepts. Exponential notation is a JSON transport
    // detail (System.Text.Json binds "1e-10" to decimal losslessly), not a
    // reason to block the user from typing a legitimate dust-level quantity.
    const control = new FormControl<number | null>(0.0000000001);
    expect(decimalPrecisionValidator(10)(control)).toBeNull();
  });

  it('still rejects a decimal count beyond the cap even at a magnitude JS would print with "e"', () => {
    const control = new FormControl<number | null>(0.00000000012); // 11dp
    const result = decimalPrecisionValidator(10)(control);
    expect(result).toEqual({ maxDecimals: { max: 10, actual: 11 } });
  });

  it('passes null/empty through — required-ness is a separate validator', () => {
    expect(decimalPrecisionValidator(10)(new FormControl<number | null>(null))).toBeNull();
  });
});

describe('positiveNumberValidator', () => {
  it('rejects zero, unlike Validators.min(0)', () => {
    expect(positiveNumberValidator()(new FormControl<number | null>(0))).toEqual({
      positive: true,
    });
  });

  it('rejects negative values', () => {
    expect(positiveNumberValidator()(new FormControl<number | null>(-5))).toEqual({
      positive: true,
    });
  });

  it('accepts a tiny positive value', () => {
    expect(positiveNumberValidator()(new FormControl<number | null>(0.0000000001))).toBeNull();
  });
});

describe('nonNegativeNumberValidator (D36 — pricePerUnit only, a free share/scrip dividend prices at exactly 0)', () => {
  it('accepts zero, unlike positiveNumberValidator', () => {
    expect(nonNegativeNumberValidator()(new FormControl<number | null>(0))).toBeNull();
  });

  it('rejects negative values', () => {
    expect(nonNegativeNumberValidator()(new FormControl<number | null>(-0.01))).toEqual({
      negative: true,
    });
  });

  it('accepts a tiny positive value', () => {
    expect(nonNegativeNumberValidator()(new FormControl<number | null>(0.0005326))).toBeNull();
  });

  it('passes null/empty through — required-ness is a separate validator', () => {
    expect(nonNegativeNumberValidator()(new FormControl<number | null>(null))).toBeNull();
  });
});
