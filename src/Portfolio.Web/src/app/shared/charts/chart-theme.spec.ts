import { foldToOther, gainLossColor, readChartTokens, seriesColor } from './chart-theme';

describe('chart-theme', () => {
  describe('readChartTokens', () => {
    it('falls back to the documented light-mode palette when no stylesheet is applied', () => {
      const tokens = readChartTokens();
      // These are the exact hexes ui.tokens.scss declares under :root — the
      // dataviz-validated default palette (see references/palette.md).
      expect(tokens.series).toEqual([
        '#2a78d6',
        '#eb6834',
        '#1baf7a',
        '#eda100',
        '#e87ba4',
        '#008300',
        '#4a3aa7',
        '#e34948',
      ]);
      expect(tokens.gain).toBe('#006300');
      expect(tokens.loss).toBe('#d03b3b');
    });

    it('reads a real custom property off the DOM when one is set', () => {
      document.documentElement.style.setProperty('--ui-color-gain', '#123456');
      try {
        const tokens = readChartTokens();
        expect(tokens.gain).toBe('#123456');
      } finally {
        document.documentElement.style.removeProperty('--ui-color-gain');
      }
    });
  });

  describe('seriesColor', () => {
    it('assigns the fixed hues in order and never past index 7', () => {
      const tokens = readChartTokens();
      expect(seriesColor(tokens, 0)).toBe(tokens.series[0]);
      expect(seriesColor(tokens, 7)).toBe(tokens.series[7]);
      // Index 8 must wrap rather than throw — callers are expected to have
      // already folded a 9th series into "Other" before this point.
      expect(seriesColor(tokens, 8)).toBe(tokens.series[0]);
    });
  });

  describe('gainLossColor', () => {
    it('colours a positive value with the gain token and a negative with loss', () => {
      const tokens = readChartTokens();
      expect(gainLossColor(tokens, 12.5)).toBe(tokens.gain);
      expect(gainLossColor(tokens, -0.01)).toBe(tokens.loss);
      expect(gainLossColor(tokens, 0)).toBe(tokens.gain);
    });
  });

  describe('foldToOther', () => {
    it('leaves 8 or fewer items untouched', () => {
      const items = Array.from({ length: 8 }, (_, i) => ({ value: i }));
      const result = foldToOther(items, (rest) => ({ value: rest.reduce((s, r) => s + r.value, 0) }));
      expect(result).toEqual(items);
    });

    it('folds the 9th+ item into a single "Other" bucket capped at 8 total slots', () => {
      const items = Array.from({ length: 10 }, (_, i) => ({ value: i }));
      const result = foldToOther(items, (rest) => ({ value: rest.reduce((s, r) => s + r.value, 0) }));
      expect(result).toHaveLength(8);
      // First 7 kept as-is, the 8th slot is "Other" summing indices 7..9 (7+8+9=24).
      expect(result.slice(0, 7)).toEqual(items.slice(0, 7));
      expect(result[7].value).toBe(24);
    });
  });
});
