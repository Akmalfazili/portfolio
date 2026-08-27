import { Directive, ElementRef, afterNextRender, inject } from '@angular/core';

/**
 * Repairs the ARIA table structure of a virtualized CSS-grid table.
 *
 * `cdk-virtual-scroll-viewport` renders two elements of its own between the
 * host and the projected rows:
 *
 * ```
 * <cdk-virtual-scroll-viewport>            <- generic, no role
 *   <div class="cdk-virtual-scroll-content-wrapper">   <- generic, no role
 *     <div role="row">…</div>
 * ```
 *
 * `role="table"` must OWN its `role="row"` children — directly, or through a
 * `rowgroup`. Two role-less generic elements in between sever that ownership,
 * so the column/row semantics the template carefully declares are not
 * reliably exposed to assistive tech at all. Tests do not catch this: every
 * `role` attribute is present and asserted, and the tree is still broken.
 *
 * The fix is structural, not cosmetic:
 *
 * - the viewport itself takes `role="presentation"`, dropping it out of the
 *   accessibility tree so its children are exposed to the nearest ancestor
 *   that is in the tree (the `role="table"`). It carries no `tabindex` and no
 *   global `aria-*` attribute, so `presentation` is honoured rather than
 *   ignored.
 * - the content wrapper becomes the `rowgroup`. CDK does not expose it as an
 *   input or a public member, so it is reached by query after first render —
 *   the one place a `setAttribute` is warranted over a template binding.
 *
 * Yielding a conformant `table > rowgroup > row`.
 *
 * Pair this with `aria-rowcount` on the table and `aria-rowindex` on each row:
 * virtualization keeps only the visible rows in the DOM, so without those a
 * screen reader announces a 12-row table when there are 140.
 */
@Directive({
  selector: 'cdk-virtual-scroll-viewport[appVirtualRowgroup]',
  standalone: true,
  host: { role: 'presentation' },
})
export class VirtualRowgroup {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  constructor() {
    afterNextRender(() => {
      this.host.nativeElement
        .querySelector('.cdk-virtual-scroll-content-wrapper')
        ?.setAttribute('role', 'rowgroup');
    });
  }
}
