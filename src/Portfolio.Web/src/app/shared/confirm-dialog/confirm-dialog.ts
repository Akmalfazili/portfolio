import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';

export interface ConfirmDialogData {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Renders the confirm button as a "warn" action — used for delete. */
  destructive?: boolean;
}

/**
 * One shared yes/no confirmation dialog — used by the transactions list
 * before a delete, so a mis-click doesn't remove a row with no way back.
 * Resolves the `MatDialogRef` with `true` on confirm, `false`/`undefined` on
 * cancel or backdrop dismiss.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [MatButtonModule, MatDialogModule],
  templateUrl: './confirm-dialog.html',
  styleUrl: './confirm-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ConfirmDialog, boolean>);

  confirm(): void {
    this.dialogRef.close(true);
  }

  cancel(): void {
    this.dialogRef.close(false);
  }
}
