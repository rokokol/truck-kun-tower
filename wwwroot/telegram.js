// Small bridge between the game and Telegram: launch, storage, sharing and the safe area.
// Everything is optional — outside Telegram the game keeps working as a plain web page.
globalThis.telegram = (() => {
  const app = globalThis.Telegram?.WebApp;
  const live = Boolean(app && (app.initData || (app.platform && app.platform !== 'unknown')));
  const cloud = live ? app.CloudStorage : undefined;
  const params = new URLSearchParams(location.search);

  const local = {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) {
      try { value == null ? localStorage.removeItem(key) : localStorage.setItem(key, value); } catch {}
    },
  };

  // Telegram callbacks may never fire (old client, closed socket), so every call has a deadline.
  const answer = (run, fallback, ms = 1200) => new Promise(resolve => {
    let done = false;
    const finish = value => { if (done) return; done = true; clearTimeout(timer); resolve(value ?? fallback); };
    const timer = setTimeout(() => finish(fallback), ms);
    try { run(finish); } catch { finish(fallback); }
  });

  function ground() {
    const value = getComputedStyle(document.body).backgroundColor;
    const parts = value.match(/\d+(\.\d+)?/g);
    if (!parts || parts.length < 3) return null;
    return '#' + parts.slice(0, 3)
      .map(part => Math.round(Number(part)).toString(16).padStart(2, '0')).join('');
  }

  function init() {
    if (!live || init.done) return;
    init.done = true;
    try { app.ready(); } catch {}
    try { app.expand(); } catch {}
    const shade = ground();
    if (shade) {
      try { app.setHeaderColor(shade); } catch {}
      try { app.setBackgroundColor(shade); } catch {}
    }
    if (app.isVersionAtLeast?.('7.7') && typeof app.disableVerticalSwipes === 'function')
      try { app.disableVerticalSwipes(); } catch {}
  }

  function startParam() {
    return params.get('room') || params.get('tgWebAppStartParam') || app?.initDataUnsafe?.start_param || null;
  }

  function buildInvite(base, room) {
    if (!base) return null;
    try {
      const url = new URL(base);
      url.searchParams.set('startapp', room);
      return url.href;
    } catch { return null; }
  }

  function share(url, text) {
    if (!live || typeof app.openTelegramLink !== 'function') return false;
    try {
      app.openTelegramLink(`https://t.me/share/url?url=${encodeURIComponent(url)}&text=${encodeURIComponent(text)}`);
      return true;
    } catch { return false; }
  }

  async function copy(text) {
    try { await navigator.clipboard.writeText(text); return true; } catch {}
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.cssText = 'position:fixed;opacity:0';
    document.body.append(area);
    area.select();
    let copied = false;
    try { copied = document.execCommand('copy'); } catch {}
    area.remove();
    return copied;
  }

  function confirmClose(on) {
    if (!live) return;
    try { on ? app.enableClosingConfirmation() : app.disableClosingConfirmation(); } catch {}
  }

  function impact(style = 'light') {
    if (!live) return;
    try { app.HapticFeedback?.impactOccurred(style); } catch {}
  }

  async function get(key) {
    const saved = local.get(key);
    if (!cloud?.getItem) return saved;
    return answer(finish => cloud.getItem(key, (error, value) => finish(error ? saved : value)), saved);
  }

  function set(key, value) {
    local.set(key, value);
    if (!cloud) return Promise.resolve();
    return answer(finish => value == null
      ? cloud.removeItem(key, finish)
      : cloud.setItem(key, value, finish));
  }

  init();
  return { live, app, init, get, set, startParam, buildInvite, share, copy, confirmClose, impact };
})();
