// Sign-in, the lobby and the live channel.
// The board belongs to game.js: the two scripts never draw each other's screen,
// they only agree on the tower:screen event and on the small Tower store below.

(function () {
  'use strict';

  const key = {
    token: 'tower:token',
    seat: id => `duel:${id}`,
  };

  const store = {
    get(name) { try { return localStorage.getItem(name); } catch { return null; } },
    set(name, value) {
      try { value == null ? localStorage.removeItem(name) : localStorage.setItem(name, value); } catch {}
    },
  };

  class ApiError extends Error {
    constructor(message, status) {
      super(message);
      this.name = 'ApiError';
      this.status = status;
    }
  }

  const fallback = status => ({
    0: 'Сеть недоступна, проверь соединение',
    400: 'Проверь введённые данные',
    401: 'Нужен вход',
    403: 'Нет доступа к этому месту',
    404: 'Не найдено',
    409: 'Место занято или условия уже изменились',
    429: 'Слишком много попыток, подожди немного',
  }[status] || `Запрос не прошёл: ${status}`);

  let token = store.get(key.token);

  // One place for the wire: bearer for an account, X-Seat-Token for a chair in a room
  async function request(path, options = {}) {
    const { method = 'GET', body, auth = true, seat } = options;
    const headers = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (auth && token) headers.Authorization = `Bearer ${token}`;
    if (seat) headers['X-Seat-Token'] = seat;
    let response;
    try {
      response = await fetch(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        credentials: 'same-origin',
      });
    } catch {
      throw new ApiError(fallback(0), 0);
    }
    if (response.status === 204) return null;
    const text = await response.text();
    let data = null;
    if (text) {
      try { data = JSON.parse(text); } catch { data = null; }
    }
    if (!response.ok) throw new ApiError(data?.detail || fallback(response.status), response.status);
    return data;
  }

  // ---- live channel -------------------------------------------------------

  const listeners = new Map();
  const events = ['Changed', 'FriendRequest', 'FriendAccepted', 'FriendRemoved', 'DuelInvite', 'InviteResolved', 'Presence'];

  function on(event, handler) {
    if (!listeners.has(event)) listeners.set(event, new Set());
    listeners.get(event).add(handler);
    return () => listeners.get(event)?.delete(handler);
  }

  function emit(event, payload) {
    for (const handler of listeners.get(event) ?? []) {
      try { handler(payload); } catch (error) { console.error(error); }
    }
  }

  let hub = null;
  let connecting = null;
  let watching = null;

  function connect() {
    if (!globalThis.signalR) return Promise.resolve(null);
    if (hub) return Promise.resolve(hub.state === 'Connected' ? hub : (connecting ?? Promise.resolve(hub)));
    if (connecting) return connecting;
    try {
      hub = new signalR.HubConnectionBuilder()
        // A guest sends an empty token, the server then treats the connection as anonymous
        .withUrl('/bell', { accessTokenFactory: () => token || '' })
        .withAutomaticReconnect()
        .build();
    } catch (error) {
      console.error(error);
      return Promise.resolve(null);
    }
    for (const name of events) hub.on(name, payload => emit(name, payload));
    hub.onreconnected(() => join().catch(() => {}));
    hub.onclose(() => { hub = null; });
    const started = hub.start()
      .then(() => join().catch(() => {}))
      .then(() => hub)
      .catch(error => {
        // The socket is a convenience: the board polls and the lobby refreshes on its own
        console.warn('Живой канал не поднялся', error?.message ?? error);
        hub = null;
        return null;
      });
    connecting = started.finally(() => { connecting = null; });
    return connecting;
  }

  async function join() {
    if (!hub || hub.state !== 'Connected' || !watching) return;
    await hub.invoke('JoinRoom', String(watching.roomId), watching.seatToken ?? null);
    // Everything that changed before the server put us in the group was broadcast
    // into the void, so the board reads the state again instead of trusting the gap
    emit('RoomJoined', watching.roomId);
  }

  async function watchRoom(roomId, seatToken) {
    watching = { roomId: String(roomId), seatToken: seatToken ?? null };
    await connect();
    await join();
  }

  async function stop() {
    watching = null;
    const old = hub;
    hub = null;
    if (old) await old.stop().catch(() => {});
  }

  // ---- seats --------------------------------------------------------------

  const seatOf = id => store.get(key.seat(String(id)));
  function remember(id, seatToken) {
    const room = String(id);
    if (seatToken) store.set(key.seat(room), seatToken);
    else store.set(key.seat(room), null);
  }
  function forgetSeat(id) {
    store.set(key.seat(String(id)), null);
  }

  function linkFor(roomId) {
    const url = new URL(location.pathname || '/', location.origin);
    url.search = '';
    url.searchParams.set('room', String(roomId));
    return url.href;
  }

  const sideName = side => (String(side).toLowerCase() === 'attacker' ? 'атакующий' : 'защитник');
  const statusName = status => ({
    waiting: 'ждём соперника',
    playing: 'идёт бой',
    finished: 'завершена',
    cancelled: 'отменена',
    abandoned: 'брошена',
  }[String(status).toLowerCase()] || String(status).toLowerCase());

  const sanctioned = [400, 403, 409];

  function tell(screen, detail = {}) {
    window.dispatchEvent(new CustomEvent('tower:screen', { detail: { screen, ...detail } }));
  }

  // ---- Alpine screens -----------------------------------------------------

  let live = null;

  function component() {
    return {
      screen: null,
      pendingRoom: '',
      busy: false,
      mode: 'login',
      nickname: '',
      password: '',
      error: '',
      user: null,
      friends: [],
      incoming: [],
      outgoing: [],
      invites: [],
      duels: [],
      side: 'defender',
      friendNick: '',
      note: '',
      lobbyError: '',

      get joinedLabel() {
        if (!this.user?.createdAt) return '—';
        const date = new Date(this.user.createdAt);
        return Number.isNaN(date.getTime())
          ? '—'
          : date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
      },

      async init() {
        live = this;
        this.pendingRoom = new URLSearchParams(location.search).get('room') ?? '';
        on('FriendRequest', payload => this.pushRequest(payload));
        on('FriendAccepted', () => this.refreshLobby());
        on('FriendRemoved', payload => this.dropFriend(payload));
        on('DuelInvite', payload => this.pushInvite(payload));
        on('InviteResolved', payload => this.resolveInvite(payload));
        on('Presence', payload => this.markPresence(payload));
        setInterval(() => {
          if (this.screen === 'lobby') {
            this.refreshLobby();
            this.refreshDuels();
          }
        }, 10000);
        try {
          await boot();
        } catch (error) {
          this.error = error?.message ?? 'Что-то пошло не так';
          this.screen = 'auth';
        }
      },

      // ---- auth ----
      async submitAuth() {
        if (this.busy) return;
        const nickname = this.nickname.trim();
        this.note = '';
        if (nickname.length < 3) { this.error = 'Ник короче трёх символов'; return; }
        if (this.password.length < 8) { this.error = 'Пароль короче восьми символов'; return; }
        if (this.password.length > 72) { this.error = 'Пароль длиннее 72 символов'; return; }
        this.busy = true;
        this.error = '';
        try {
          const path = this.mode === 'login' ? '/api/auth/login' : '/api/auth/register';
          const answer = await request(path, { method: 'POST', body: { nickname, password: this.password }, auth: false });
          token = answer.token;
          store.set(key.token, token);
          this.user = answer.user;
          this.password = '';
          await this.enterLobby();
        } catch (error) {
          this.error = error.message;
        } finally {
          this.busy = false;
        }
      },

      async playGuest() {
        if (this.busy) return;
        this.busy = true;
        this.error = '';
        try {
          if (this.pendingRoom) {
            const room = this.pendingRoom;
            this.pendingRoom = '';
            await this.joinRoom(room, token ? 'lobbyError' : 'error');
            return;
          }
          const answer = await request('/api/duels', { method: 'POST', body: { side: 'defender' }, auth: !token });
          remember(answer.id, answer.seatToken);
          this.openBoard(answer.id, answer.seatToken ?? null);
        } catch (error) {
          this.error = error.message;
        } finally {
          this.busy = false;
        }
      },

      async logout() {
        token = null;
        store.set(key.token, null);
        this.user = null;
        this.friends = [];
        this.incoming = [];
        this.outgoing = [];
        this.invites = [];
        this.duels = [];
        this.password = '';
        this.error = '';
        this.lobbyError = '';
        this.note = '';
        this.screen = 'auth';
        this.mode = 'login';
        history.replaceState(null, '', location.pathname);
        await stop();
      },

      // ---- lobby ----
      async enterLobby() {
        this.screen = 'lobby';
        this.error = '';
        history.replaceState(null, '', location.pathname);
        await stop();
        await connect();
        await Promise.all([this.refreshLobby(), this.refreshDuels()]);
        if (this.pendingRoom) {
          const room = this.pendingRoom;
          this.pendingRoom = '';
          await this.joinRoom(room, 'lobbyError');
        }
      },

      async refreshLobby() {
        if (!token) return;
        try {
          const [friends, requests, invites] = await Promise.all([
            request('/api/friends'),
            request('/api/friends/requests'),
            request('/api/invites'),
          ]);
          this.friends = friends ?? [];
          this.incoming = requests?.incoming ?? [];
          this.outgoing = requests?.outgoing ?? [];
          this.invites = invites ?? [];
        } catch (error) {
          if (error.status === 401) this.expire(error.message);
          else this.lobbyError = error.message;
        }
      },

      async refreshDuels() {
        if (!token) return;
        try {
          this.duels = (await request('/api/duels')) ?? [];
        } catch (error) {
          if (error.status === 401) this.expire(error.message);
        }
      },

      async addFriend() {
        const nickname = this.friendNick.trim();
        if (!nickname) return;
        this.busy = true;
        this.lobbyError = '';
        this.note = '';
        try {
          await request('/api/friends/requests', { method: 'POST', body: { nickname } });
          this.friendNick = '';
          this.note = `Заявка отправлена: ${nickname}`;
          await this.refreshLobby();
        } catch (error) {
          this.lobbyError = error.message;
        } finally {
          this.busy = false;
        }
      },

      async answerRequest(requestId, accept) {
        if (this.busy) return;
        this.busy = true;
        this.lobbyError = '';
        try {
          const path = `/api/friends/requests/${encodeURIComponent(requestId)}${accept ? '/accept' : ''}`;
          await request(path, { method: accept ? 'POST' : 'DELETE' });
          await this.refreshLobby();
        } catch (error) {
          this.lobbyError = error.message;
        } finally {
          this.busy = false;
        }
      },

      async removeFriend(friend) {
        if (!confirm(`Удалить ${friend.nickname} из друзей?`)) return;
        this.busy = true;
        this.lobbyError = '';
        try {
          await request(`/api/friends/${encodeURIComponent(friend.id)}`, { method: 'DELETE' });
          this.friends = this.friends.filter(row => row.id !== friend.id);
        } catch (error) {
          this.lobbyError = error.message;
        } finally {
          this.busy = false;
        }
      },

      async acceptInvite(invite) {
        if (this.busy) return;
        this.busy = true;
        this.lobbyError = '';
        try {
          const answer = await request(`/api/invites/${encodeURIComponent(invite.id)}/accept`, { method: 'POST' });
          this.invites = this.invites.filter(row => row.id !== invite.id);
          await this.joinRoom(answer?.roomId ?? invite.roomId, 'lobbyError');
        } catch (error) {
          this.lobbyError = error.message;
        } finally {
          this.busy = false;
        }
      },

      async declineInvite(invite) {
        this.lobbyError = '';
        try {
          await request(`/api/invites/${encodeURIComponent(invite.id)}`, { method: 'DELETE' });
          this.invites = this.invites.filter(row => row.id !== invite.id);
        } catch (error) {
          this.lobbyError = error.message;
        }
      },

      async createDuel(friendId) {
        if (this.busy) return;
        this.busy = true;
        this.lobbyError = '';
        this.note = '';
        try {
          const answer = await request('/api/duels', { method: 'POST', body: { side: this.side } });
          remember(answer.id, answer.seatToken);
          if (friendId) {
            try {
              await request('/api/invites', { method: 'POST', body: { friendId, roomId: answer.id } });
            } catch (error) {
              this.note = `Дуэль создана, но приглашение не ушло: ${error.message}`;
            }
          }
          this.openBoard(answer.id, answer.seatToken ?? null);
        } catch (error) {
          this.lobbyError = error.message;
        } finally {
          this.busy = false;
        }
      },

      inviteFriend(friend) {
        return this.createDuel(friend.id);
      },

      async continueDuel(duel) {
        const status = String(duel.status).toLowerCase();
        if (['finished', 'cancelled', 'abandoned'].includes(status)) {
          this.lobbyError = 'Эта дуэль уже закрыта';
          return;
        }
        this.openBoard(duel.id, seatOf(duel.id));
      },

      async cancelDuel(duel) {
        if (!confirm('Закрыть комнату? Соперник её больше не откроет.')) return;
        this.busy = true;
        this.lobbyError = '';
        try {
          await request(`/api/duels/${encodeURIComponent(duel.id)}`, { method: 'DELETE' });
          forgetSeat(duel.id);
          this.duels = this.duels.filter(row => row.id !== duel.id);
        } catch (error) {
          this.lobbyError = error.message;
        } finally {
          this.busy = false;
        }
      },

      async copyLink(roomId) {
        const text = linkFor(roomId);
        try {
          await navigator.clipboard.writeText(text);
          this.note = 'Ссылка скопирована';
        } catch {
          this.note = text;
        }
      },

      statusOf: statusName,
      sideOf: sideName,

      // ---- hub payloads ----
      pushRequest(payload) {
        const row = payload?.request;
        if (!row?.id || this.incoming.some(item => item.id === row.id)) return;
        this.incoming = [row, ...this.incoming];
        this.note = `Заявка в друзья от ${row.from?.nickname ?? 'игрока'}`;
      },

      pushInvite(payload) {
        const row = payload?.invite;
        if (!row?.id || this.invites.some(item => item.id === row.id)) return;
        this.invites = [row, ...this.invites];
        this.note = `${row.from?.nickname ?? 'Соперник'} зовёт в дуэль`;
      },

      resolveInvite(payload) {
        if (!payload?.id) return;
        this.invites = this.invites.filter(row => row.id !== payload.id);
        if (payload.status === 'accepted' || payload.status === 'Accepted') {
          this.note = 'Приглашение приняли — соперник уже в комнате';
        }
      },

      dropFriend(payload) {
        if (!payload?.userId) return;
        this.friends = this.friends.filter(row => row.id !== payload.userId);
      },

      markPresence(payload) {
        if (!payload?.userId) return;
        const friend = this.friends.find(row => row.id === payload.userId);
        if (friend) friend.online = Boolean(payload.online);
      },

      expire(message) {
        token = null;
        store.set(key.token, null);
        this.user = null;
        this.error = message || 'Сессия больше не действует, войди заново';
        this.lobbyError = '';
        this.screen = 'auth';
        this.mode = 'login';
        history.replaceState(null, '', location.pathname);
        stop();
      },

      // ---- board handover ----
      async joinRoom(roomId, errorSlot) {
        const slot = errorSlot === 'error' ? 'error' : 'lobbyError';
        const id = String(roomId ?? '').trim();
        if (!id) { this[slot] = 'Ссылка без комнаты'; return; }
        // A chair answers to the seat token or to the account, so an old seat opens without joining
        if (await this.canOpen(id)) {
          this.openBoard(id, seatOf(id));
          return;
        }
        const first = this.side === 'attacker' ? 'attacker' : 'defender';
        const order = [first, first === 'attacker' ? 'defender' : 'attacker'];
        let last = null;
        for (const side of order) {
          try {
            const answer = await request(`/api/duels/${encodeURIComponent(id)}/join`, {
              method: 'POST',
              body: { side },
            });
            remember(id, answer?.seatToken);
            this.openBoard(id, answer?.seatToken ?? null);
            return;
          } catch (error) {
            last = error;
            // A taken chair is worth one more try on the other side; anything else is final
            if (!sanctioned.includes(error.status)) break;
          }
        }
        this[slot] = last?.message ?? 'Не удалось войти в комнату';
      },

      async canOpen(roomId) {
        if (!token && !seatOf(roomId)) return false;
        try {
          await request(`/api/duels/${encodeURIComponent(roomId)}/state`, { seat: seatOf(roomId) });
          return true;
        } catch {
          return false;
        }
      },

      openBoard(roomId, seatToken) {
        remember(roomId, seatToken);
        this.screen = 'game';
        tell('game', { roomId: String(roomId), seatToken: seatToken ?? null });
      },
    };
  }

  async function boot() {
    const params = new URLSearchParams(location.search);
    const room = params.get('room');
    if (room) live.pendingRoom = room;
    if (token) {
      try {
        live.user = await request('/api/auth/me');
      } catch (error) {
        if (error.status === 401 || error.status === 403) token = null;
        else live.error = error.message;
        if (!live.user) store.set(key.token, null);
      }
    }
    if (token && live.user) {
      await live.enterLobby();
      return;
    }
    token = null;
    // A reload in a guest room keeps the chair: the seat token in this browser is the whole credential
    if (live.pendingRoom && seatOf(live.pendingRoom) && await live.canOpen(live.pendingRoom)) {
      const held = live.pendingRoom;
      live.pendingRoom = '';
      live.openBoard(held, seatOf(held));
      return;
    }
    live.screen = 'auth';
  }

  // The board says it is done and the lobby takes the screen back; auth wins when there is no account
  window.addEventListener('tower:screen', event => {
    const detail = event.detail ?? {};
    if (detail.screen !== 'lobby' || !live) return;
    history.replaceState(null, '', location.pathname);
    if (live.user) {
      live.screen = 'lobby';
      if (detail.message) live.lobbyError = detail.message;
      live.refreshLobby();
      live.refreshDuels();
    } else {
      live.screen = 'auth';
      if (detail.message) live.error = detail.message;
    }
  });

  document.addEventListener('alpine:init', () => {
    Alpine.data('tower', component);
  });

  // Shared with game.js: one wire, one socket, one idea of where a seat is kept
  globalThis.Tower = {
    request,
    on,
    connect,
    watchRoom,
    seatOf,
    remember,
    forgetSeat,
    linkFor,
    connected: () => hub?.state === 'Connected',
    get token() { return token; },
  };
})();
