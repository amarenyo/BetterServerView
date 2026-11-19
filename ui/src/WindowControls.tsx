import { useEffect, useState } from 'react';
import { api, getPlatform, isNative, onEvent } from './bridge';

/** true while the app draws its own title bar (Windows); false when the system's title bar is used (Linux). */
export function useCustomTitleBar(): boolean {
  const [custom, setCustom] = useState(true);
  useEffect(() => {
    let alive = true;
    void getPlatform().then((p) => {
      if (!alive) return;
      setCustom(p.customTitleBar);
      document.documentElement.classList.toggle('sys-titlebar', !p.customTitleBar);
      document.documentElement.classList.add('platform-' + p.platform);
    });
    return () => {
      alive = false;
    };
  }, []);
  return custom;
}

/** Whether the window is maximized (Windows app only; always false in the browser). Also sets the class "win-max" on the root element. */
export function useWindowMaximized(): boolean {
  const [maximized, setMaximized] = useState(false);

  useEffect(() => {
    if (!isNative) return;
    let alive = true;
    api
      .windowCommand('state')
      .then((r) => alive && setMaximized(r.maximized))
      .catch(() => undefined);
    const off = onEvent((e) => {
      if (e.event === 'window.state') setMaximized(e.maximized);
    });
    return () => {
      alive = false;
      off();
    };
  }, []);

  useEffect(() => {
    document.documentElement.classList.toggle('win-max', maximized);
  }, [maximized]);

  return maximized;
}

/** Minimize, maximize/restore and close for the frameless window. */
export function WindowControls() {
  const maximized = useWindowMaximized();
  const custom = useCustomTitleBar();
  if (!isNative || !custom) return null;

  return (
    <div className="win-ctl">
      <button className="win-btn" title="Minimize" aria-label="Minimize" onClick={() => void api.windowCommand('minimize')}>
        <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
          <path d="M0 5.5h10" stroke="currentColor" strokeWidth="1" fill="none" />
        </svg>
      </button>
      <button
        className="win-btn"
        title={maximized ? 'Restore' : 'Maximize'}
        aria-label={maximized ? 'Restore' : 'Maximize'}
        onClick={() => void api.windowCommand('maximize')}
      >
        {maximized ? (
          <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
            <path d="M2.5 2.5V.5h7v7h-2M.5 2.5h7v7h-7z" stroke="currentColor" strokeWidth="1" fill="none" />
          </svg>
        ) : (
          <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
            <rect x=".5" y=".5" width="9" height="9" stroke="currentColor" strokeWidth="1" fill="none" />
          </svg>
        )}
      </button>
      <button className="win-btn close" title="Close" aria-label="Close" onClick={() => void api.windowCommand('close')}>
        <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
          <path d="M0 0l10 10M10 0L0 10" stroke="currentColor" strokeWidth="1" fill="none" />
        </svg>
      </button>
    </div>
  );
}

/** Slim title bar for screens without a tab bar (e.g. unlock): for dragging the window plus window buttons. */
export function TitleStrip() {
  const custom = useCustomTitleBar();
  if (!isNative || !custom) return null;
  return (
    <div className="title-strip">
      <span className="grow" />
      <WindowControls />
    </div>
  );
}
