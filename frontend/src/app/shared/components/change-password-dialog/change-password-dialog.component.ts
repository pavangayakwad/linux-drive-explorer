import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { isAxiosError } from 'axios';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { MessageModule } from 'primeng/message';
import { PasswordModule } from 'primeng/password';
import { AuthService } from '../../../core/services/auth.service';

@Component({
  selector: 'app-change-password-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, DialogModule, PasswordModule, ButtonModule, MessageModule],
  templateUrl: './change-password-dialog.component.html',
  styleUrl: './change-password-dialog.component.scss',
})
export class ChangePasswordDialogComponent {
  @Input() visible = false;
  @Output() readonly visibleChange = new EventEmitter<boolean>();

  newPassword = '';
  confirmPassword = '';

  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  constructor(private readonly authService: AuthService) {}

  close(): void {
    this.reset();
    this.visibleChange.emit(false);
  }

  private reset(): void {
    this.newPassword = '';
    this.confirmPassword = '';
    this.errorMessage.set(null);
    this.saving.set(false);
  }

  async submit(): Promise<void> {
    this.errorMessage.set(null);

    if (this.newPassword.length < 8) {
      this.errorMessage.set('New password must be at least 8 characters long.');
      return;
    }
    if (this.newPassword !== this.confirmPassword) {
      this.errorMessage.set('New password and confirmation do not match.');
      return;
    }

    this.saving.set(true);
    try {
      await this.authService.changePassword(this.newPassword);
      // authService.changePassword() already signs the user out and redirects to /login on success.
    } catch (error) {
      const message = isAxiosError(error) && error.response?.data?.message
        ? (error.response.data.message as string)
        : 'Could not change the password.';
      this.errorMessage.set(message);
    } finally {
      this.saving.set(false);
    }
  }
}
