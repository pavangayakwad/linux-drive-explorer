import { AfterViewInit, Component, ElementRef, EventEmitter, Input, OnDestroy, Output, ViewChild } from '@angular/core';
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import { tokenStore } from '../../../core/http/token-store';

const EARLY_CLOSE_MS = 3000;

/** Interactive shell backed by the API's /ws/terminal WebSocket. The working directory is fixed at
 * creation time (`path`) - later navigation in the explorer deliberately does not affect it. */
@Component({
  selector: 'app-terminal',
  standalone: true,
  template: `
    <div class="terminal__header">
      <i class="pi pi-code"></i>
      <span class="terminal__title">Terminal</span>
      <button type="button" class="terminal__close" aria-label="Close terminal" (click)="closed.emit()">
        <i class="pi pi-times"></i>
      </button>
    </div>
    <div #host class="terminal__host"></div>
  `,
  styleUrl: './terminal.component.scss',
})
export class TerminalComponent implements AfterViewInit, OnDestroy {
  @Input({ required: true }) path = '/';
  @Output() readonly closed = new EventEmitter<void>();
  @ViewChild('host', { static: true }) private readonly hostRef!: ElementRef<HTMLDivElement>;

  private term!: Terminal;
  private readonly fit = new FitAddon();
  private socket: WebSocket | null = null;
  private observer: ResizeObserver | null = null;
  private resizeTimer: ReturnType<typeof setTimeout> | null = null;
  private disposed = false;

  ngAfterViewInit(): void {
    this.term = new Terminal({
      cursorBlink: true,
      fontFamily: "'Cascadia Code', Consolas, monospace",
      fontSize: 13,
      scrollback: 5000,
      theme: { background: '#1e1e1e', foreground: '#d4d4d4' },
    });
    this.term.loadAddon(this.fit);
    // Ctrl+C copies when text is selected (otherwise it stays SIGINT); Ctrl+V pastes. The
    // Ctrl+Shift+C/V variants always copy/paste. Returning false stops xterm sending the key to the
    // shell; for paste the browser's native paste event then feeds xterm.
    this.term.attachCustomKeyEventHandler((e) => {
      if (e.type !== 'keydown' || !(e.ctrlKey || e.metaKey) || e.altKey) {
        return true;
      }
      const key = e.key.toLowerCase();
      if (key === 'c' && (e.shiftKey || this.term.hasSelection())) {
        if (this.term.hasSelection()) {
          void navigator.clipboard?.writeText(this.term.getSelection());
        }
        e.preventDefault();
        return false;
      }
      if (key === 'v') {
        // Let the native paste event run (preventing default here would block it).
        return false;
      }
      return true;
    });
    this.term.open(this.hostRef.nativeElement);
    this.fit.fit();
    this.connect();

    this.term.onData((data) => {
      if (this.socket?.readyState === WebSocket.OPEN) {
        this.socket.send(data);
      }
    });
    this.observer = new ResizeObserver(() => this.scheduleFit());
    this.observer.observe(this.hostRef.nativeElement);
    this.term.focus();
  }

  private connect(): void {
    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    const query = new URLSearchParams({
      path: this.path,
      cols: String(this.term.cols),
      rows: String(this.term.rows),
      access_token: tokenStore.accessToken ?? '',
    });
    const socket = new WebSocket(`${scheme}://${location.host}/ws/terminal?${query}`);
    socket.binaryType = 'arraybuffer';
    this.socket = socket;

    socket.onmessage = (event) => {
      this.term.write(typeof event.data === 'string' ? event.data : new Uint8Array(event.data as ArrayBuffer));
    };
    const openedAt = Date.now();
    // The server closes the socket when the shell exits (`exit` / Ctrl+D), so dismiss the panel.
    // But if it closes almost immediately the session failed to start (auth/role rejection, no
    // `script`, non-Linux host...) - keep the panel open so the error is readable instead of
    // flashing away.
    socket.onclose = (event) => {
      if (this.disposed) {
        return;
      }
      if (Date.now() - openedAt < EARLY_CLOSE_MS) {
        const reason = event.reason ? ` (${event.reason})` : '';
        this.term.write(`\r\n\x1b[31m[Terminal session ended immediately${reason}. Code ${event.code}. ` +
          'Check that you are an Admin and that the API runs on Linux with `script` installed.]\x1b[0m\r\n');
        return;
      }
      this.closed.emit();
    };
  }

  private scheduleFit(): void {
    if (this.resizeTimer) {
      clearTimeout(this.resizeTimer);
    }
    this.resizeTimer = setTimeout(() => {
      if (this.disposed || this.hostRef.nativeElement.clientHeight === 0) {
        return;
      }
      this.fit.fit();
      if (this.socket?.readyState === WebSocket.OPEN) {
        this.socket.send(JSON.stringify({ type: 'resize', cols: this.term.cols, rows: this.term.rows }));
      }
    }, 50);
  }

  ngOnDestroy(): void {
    this.disposed = true;
    if (this.resizeTimer) {
      clearTimeout(this.resizeTimer);
    }
    this.observer?.disconnect();
    this.socket?.close();
    this.term?.dispose();
  }
}
