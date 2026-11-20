import { useEffect, useRef, useState } from 'react';
import { api } from './bridge';
import { SessionFrame } from './SessionFrame';
import type { Server, SessionStatus } from './types';

/**
 * RDP session drawn by the UI itself (IronRDP/WebAssembly in a canvas). Used where the native Windows RDP control
 * does not exist (Linux). The host only provides a local WebSocket proxy that does TCP + TLS for the page.
 */
export function RdpWebView({
  tabId,
  server,
  reconnectKey,
  onStatus,
  onReconnect,
  onDuplicate,
  onEdit,
}: {
  tabId: string;
  server: Server;
  active: boolean;
  reconnectKey: number;
  onStatus: (tabId: string, status: SessionStatus) => void;
  onReconnect: () => void;
  onDuplicate: () => void;
  onEdit: () => void;
}) {
  const boxRef = useRef<HTMLDivElement>(null);
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [message, setMessage] = useState('');

  useEffect(() => {
    let cancelled = false;
    let ui: any = null;
    let element: HTMLElement | null = null;
    let resizeTimer: number | undefined;
    const setStatus = (s: SessionStatus, msg = '') => {
      if (cancelled) return;
      setStatusState(s);
      setMessage(msg);
      onStatus(tabId, s);
    };
    setStatus('connecting');

    const box = boxRef.current!;
    const ro = new ResizeObserver(() => {
      window.clearTimeout(resizeTimer);
      resizeTimer = window.setTimeout(() => {
        const r = box.getBoundingClientRect();
        if (ui && r.width > 100 && r.height > 100) {
          try {
            ui.resize(Math.round(r.width) & ~3, Math.round(r.height));
          } catch {
            /* the server does not support resizing */
          }
        }
      }, 300);
    });
    ro.observe(box);

    void (async () => {
      try {
        const [{ Backend, init, displayControl }] = await Promise.all([import('@devolutions/iron-remote-desktop-rdp'), import('@devolutions/iron-remote-desktop')]);
        await init('warn');
        if (cancelled) return;

        const info = await api.rdpWeb(tabId, server.id);
        if (cancelled) return;
        if (!info.password) throw new Error('This host has no saved password. Edit the host and enter one to connect inside the app.');

        element = document.createElement('iron-remote-desktop');
        element.setAttribute('scale', 'fit');
        element.setAttribute('flexcenter', 'true');
        (element as any).module = Backend;
        element.style.cssText = 'display:block;width:100%;height:100%';
        box.appendChild(element);

        ui = await new Promise<any>((resolve) => element!.addEventListener('ready', (e) => resolve((e as CustomEvent).detail.irgUserInteraction), { once: true }));
        if (cancelled) return;

        const r = box.getBoundingClientRect();
        // xrdp and some servers need a width that is a multiple of 4
        const width = Math.max(800, Math.round(r.width) & ~3);
        const height = Math.max(600, Math.round(r.height));
        const config = ui
          .configBuilder()
          .withUsername(info.username)
          .withPassword(info.password)
          .withServerDomain(info.domain)
          .withDestination(`${info.host}:${server.port || 3389}`)
          .withProxyAddress(info.url)
          .withAuthToken('local')
          .withDesktopSize({ width, height })
          .withExtension(displayControl(true))
          .build();
        ui.setEnableClipboard(true);
        const session = await ui.connect(config);
        if (cancelled) {
          try {
            ui.shutdown();
          } catch {
            /* already shut down */
          }
          return;
        }
        ui.setVisibility(true);
        setStatus('connected');
        void session.run().then(
          (end: { reason: () => string }) => setStatus('closed', end.reason()),
          (err: unknown) => setStatus('closed', describe(err)),
        );
      } catch (err) {
        setStatus('error', describe(err));
      }
    })();

    return () => {
      cancelled = true;
      ro.disconnect();
      window.clearTimeout(resizeTimer);
      try {
        ui?.shutdown();
      } catch {
        /* already closed */
      }
      element?.remove();
      void api.rdpClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey]);

  return (
    <SessionFrame variant="bar" server={server} status={status} onReconnect={onReconnect} onDuplicate={onDuplicate} onEdit={onEdit}>
      <div className="rdp-box rdp-web" ref={boxRef}>
        {status !== 'connected' && (
          <div className="overlay-msg">
            {status === 'connecting' && <div className="muted">Connecting to {server.host} …</div>}
            {(status === 'error' || status === 'closed') && (
              <>
                <div className={status === 'error' ? 'error' : 'muted'}>{message || 'Disconnected.'}</div>
                <button className="primary" onClick={onReconnect}>
                  Reconnect
                </button>
              </>
            )}
          </div>
        )}
      </div>
    </SessionFrame>
  );
}

/** IronRDP errors carry a kind; translate the common ones. */
function describe(err: unknown): string {
  const e = err as { kind?: () => number; backtrace?: () => string; message?: string };
  try {
    switch (e?.kind?.()) {
      case 1:
      case 2:
        return 'Authentication failed. Check the user name and password of this host.';
      case 3:
        return 'Access denied by the server.';
      case 4:
      case 5:
        return 'Could not reach the RDP server. Check the address, the port and the firewall.';
      case 6:
        return 'The server and the client could not agree on a security protocol (NLA/TLS).';
    }
    const bt = e?.backtrace?.();
    if (bt) return bt.split('\n')[0];
  } catch {
    /* fall through */
  }
  return e?.message ?? String(err);
}
