import { Injectable, signal } from '@angular/core';

/** Lets any page ask the shell to open the terminal panel in a given directory. */
@Injectable({ providedIn: 'root' })
export class TerminalRequestService {
  /** Latest request; `id` changes on every call so repeated requests for the same path still fire. */
  readonly request = signal<{ id: number; path: string } | null>(null);
  private nextId = 1;

  open(path: string): void {
    this.request.set({ id: this.nextId++, path });
  }
}
