/**
 * Matches `--ui-size-table-row-height` in `ui.tokens.scss`. `cdk-virtual-scroll-viewport`'s
 * fixed-size strategy (`[itemSize]`) needs a real JS number for its internal
 * scroll-offset math — it cannot read a CSS custom property — so this is a
 * deliberate, documented duplicate of the token's pixel value, the same kind
 * of exception `ui.mixins.scss`'s breakpoint variables already are. Keep the
 * two in sync if the row height ever changes.
 */
export const TRANSACTION_ROW_HEIGHT_PX = 44;
