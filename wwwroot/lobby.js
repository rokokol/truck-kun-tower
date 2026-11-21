// Sign-in, the lobby and the live channel.
// The board belongs to game.js: the two scripts never draw each other's screen,
// they only agree on the tower:screen event and on the small Tower store below.

(function () {
  'use strict';

  const { message, feedback } = i18n;
  const pageSize = 5;
  const historyPaths = {duels: '/api/duels', matches: '/api/matches/mine'};
  const newPager = () => ({index: 0, more: false, loading: false});

  const key = {
    token: 'tower:token',
    guestRoom: 'tower:guest-room',
    seat: id => `duel:${id}`,
  };

  // Guest seats are not shared across tabs.
  const store = {
    get(name) { try { return sessionStorage.getItem(name); } catch { return null; } },
    set(name, value) {
      try { value == null ? sessionStorage.removeItem(name) : sessionStorage.setItem(name, value); } catch {}
    },
  };

  class ApiError extends Error {
    constructor(status, detail, code) {
      super(detail || `HTTP ${status}`);
      this.name = 'ApiError';
      this.status = status;
      this.feedback = i18n.http(status, detail, code);
    }
  }

  function sharedToken() {
    try { return localStorage.getItem(key.token); } catch { return undefined; }
  }

  function persistToken(value) {
    try {
      // Keep a sign-out marker so an old tab cannot restore its previous login.
      localStorage.setItem(key.token, value ?? '');
      store.set(key.token, null);
    } catch {
      store.set(key.token, value);
    }
  }

  const shared = sharedToken();
  let token = shared == null ? store.get(key.token) : shared || null;
  if (shared === null && token) persistToken(token);
  else if (shared !== undefined) store.set(key.token, null);

  window.addEventListener('storage', event => {
    if (event.storageArea !== localStorage || (event.key !== key.token && event.key !== null)) return;
    if ((sharedToken() || null) !== token) location.reload();
  });

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
      throw new ApiError(0);
    }
    if (response.status === 204) return null;
    const text = await response.text();
    let data = null;
    if (text) {
      try { data = JSON.parse(text); } catch { data = null; }
    }
    if (!response.ok) throw new ApiError(response.status, data?.detail, data?.code);
    return data;
  }

  // ---- live channel -------------------------------------------------------

  const listeners = new Map();
  const events = ['Changed', 'RoomAbandoned', 'FriendRequest', 'FriendAccepted', 'FriendRemoved', 'DuelInvite', 'InviteResolved', 'Presence'];

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
    const identity = token;
    let socket;
    try {
      socket = new signalR.HubConnectionBuilder()
        .withUrl('/bell', { accessTokenFactory: () => identity || '' })
        .withAutomaticReconnect()
        .build();
    } catch (error) {
      console.error(error);
      return Promise.resolve(null);
    }
    hub = socket;
    for (const name of events) socket.on(name, payload => { if (hub === socket) emit(name, payload); });
    socket.onreconnected(() => { if (hub === socket) join().catch(() => {}); });
    socket.onclose(() => { if (hub === socket) hub = null; });
    const started = socket.start()
      .then(() => hub === socket ? socket : null)
      .catch(error => {
        if (hub === socket) {
          console.warn('Live channel unavailable', error?.message ?? error);
          hub = null;
        }
        return null;
      }).finally(() => { if (connecting === started) connecting = null; });
    connecting = started;
    return started;
  }

  async function join() {
    if (!hub || hub.state !== 'Connected' || !watching) return;
    const socket = hub, room = watching;
    await socket.invoke('JoinRoom', room.roomId, room.seatToken);
    // Everything that changed before the server put us in the group was broadcast
    // into the void, so the board reads the state again instead of trusting the gap
    if (hub === socket && watching === room) emit('RoomJoined', room.roomId);
  }

  async function watchRoom(roomId, seatToken) {
    watching = { roomId: String(roomId), seatToken: seatToken ?? null };
    await connect();
    await join();
  }

  function unwatchRoom(roomId) {
    if (watching?.roomId !== roomId) return;
    watching = null;
    if (hub?.state === 'Connected') hub.invoke('LeaveRoom', roomId).catch(() => {});
  }

  async function stop() {
    watching = null;
    const old = hub;
    hub = null;
    connecting = null;
    if (old) await old.stop().catch(() => {});
  }

  // ---- seats --------------------------------------------------------------

  const seatOf = id => store.get(key.seat(String(id)));
  const createGuestRoom = seat => request('/api/duels', {
    method: 'POST', body: { side: 'defender' }, auth: false, seat,
  });
  function remember(id, seatToken) {
    const room = String(id);
    store.set(key.seat(room), seatToken || null);
    if (!token && seatToken) store.set(key.guestRoom, room);
  }
  function forgetSeat(id) {
    store.set(key.seat(String(id)), null);
    if (store.get(key.guestRoom) === id) store.set(key.guestRoom, null);
  }

  function linkFor(roomId) {
    const url = new URL(location.pathname || '/', location.origin);
    url.search = '';
    url.searchParams.set('room', String(roomId));
    return url.href;
  }

  const duelStatuses = new Set(['waiting', 'playing', 'finished', 'cancelled', 'abandoned', 'interrupted']);

  function tell(screen, detail = {}) {
    window.dispatchEvent(new CustomEvent('tower:screen', { detail: { screen, ...detail } }));
  }

  // ---- Alpine screens -----------------------------------------------------

  let live = null, search = null;

  function component() {
    return {
      screen: null,
      pendingRoom: '',
      sessionCheckFailed: false,
      busy: false,
      mode: 'login',
      nickname: '',
      password: '',
      error: '',
      user: null,
      friends: [],
      onlineCount: null,
      onlinePlayers: [],
      onlineSample: null,
      incoming: [],
      outgoing: [],
      invites: [],
      duels: [],
      matches: [],
      historyPages: {duels: newPager(), matches: newPager()},
      side: 'defender',
      randomOpponent: true,
      searching: false,
      searchLocked: false,
      searchTime: '00:00',
      friendNick: '',
      note: '',
      lobbyError: '',

      lang: i18n.lang,

      t(key, params) { return i18n.t(key, params, this.lang); },
      text(value) { return i18n.text(value, this.lang); },
      inviteLabel(invite) {
        return this.t('invitedBy', { name: invite.from?.nickname ?? message('opponentName') });
      },

      get joinedLabel() {
        if (!this.user?.createdAt) return '—';
        const date = new Date(this.user.createdAt);
        return Number.isNaN(date.getTime())
          ? '—'
          : date.toLocaleDateString(i18n.locales[this.lang], { day: 'numeric', month: 'long', year: 'numeric' });
      },

      async init() {
        live = this;
        i18n.onChange(lang => { this.lang = lang; });
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
            this.refreshOnline();
          }
        }, 10000);
        try {
          await boot();
        } catch (error) {
          this.error = feedback(error);
          this.screen = 'auth';
        }
      },

      // ---- auth ----
      async submitAuth() {
        if (this.busy) return;
        const nickname = this.nickname.trim();
        this.note = '';
        if (nickname.length < 3) { this.error = message('shortName'); return; }
        if (this.password.length < 8) { this.error = message('shortPassword'); return; }
        if (this.password.length > 72) { this.error = message('longPassword'); return; }
        this.busy = true;
        this.error = '';
        try {
          const path = this.mode === 'login' ? '/api/auth/login' : '/api/auth/register';
          const answer = await request(path, { method: 'POST', body: { nickname, password: this.password }, auth: false });
          token = answer.token;
          persistToken(token);
          this.user = answer.user;
          this.password = '';
          await this.enterLobby();
        } catch (error) {
          this.error = feedback(error);
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
          const held = store.get(key.guestRoom);
          let seat = held ? seatOf(held) : null;
          if (seat) {
            try {
              const status = await request(`/api/duels/${encodeURIComponent(held)}/state`, {auth: false, seat});
              if (!status.done) {
                this.openBoard(held, seat);
                return;
              }
            } catch (error) {
              if (![403, 404].includes(error.status)) throw error;
              forgetSeat(held);
              seat = null;
            }
          }
          const answer = await createGuestRoom(seat);
          this.openBoard(answer.id, answer.seatToken ?? null);
        } catch (error) {
          this.error = feedback(error);
        } finally {
          this.busy = false;
        }
      },

      async logout() {
        if (this.busy) return;
        this.busy = true;
        token = null;
        persistToken(null);
        this.user = null;
        this.friends = [];
        this.onlineCount = this.onlineSample = null;
        this.onlinePlayers = [];
        this.incoming = [];
        this.outgoing = [];
        this.invites = [];
        this.duels = [];
        this.matches = [];
        this.historyPages = {duels: newPager(), matches: newPager()};
        this.randomOpponent = true;
        this.password = '';
        this.error = '';
        this.lobbyError = '';
        this.note = '';
        this.screen = 'auth';
        this.mode = 'login';
        history.replaceState(null, '', location.pathname);
        try { await stop(); }
        finally { this.busy = false; }
      },

      // ---- lobby ----
      async enterLobby() {
        this.screen = 'lobby';
        this.error = '';
        history.replaceState(null, '', location.pathname);
        await stop();
        await connect();
        await Promise.all([this.refreshLobby(), this.refreshDuels(), this.refreshOnline(), this.refreshMatches()]);
        if (this.pendingRoom) {
          const room = this.pendingRoom;
          this.pendingRoom = '';
          await this.joinRoom(room, 'lobbyError');
        }
      },

      async refreshLobby() {
        const identity = token;
        if (!identity) return;
        try {
          const [friends, requests, invites] = await Promise.all([
            request('/api/friends'),
            request('/api/friends/requests'),
            request('/api/invites'),
          ]);
          if (token !== identity) return;
          this.friends = friends ?? [];
          this.incoming = requests?.incoming ?? [];
          this.outgoing = requests?.outgoing ?? [];
          this.invites = invites ?? [];
        } catch (error) {
          if (token !== identity) return;
          if (error.status === 401) this.expire(feedback(error));
          else this.lobbyError = feedback(error);
        }
      },

      refreshDuels() { return this.loadHistoryPage('duels'); },
      refreshMatches() { return this.loadHistoryPage('matches'); },

      shiftHistory(kind, delta) {
        const page = this.historyPages[kind];
        if (page.loading || (delta > 0 && !page.more) || page.index + delta < 0) return;
        return this.loadHistoryPage(kind, page.index + delta);
      },

      async loadHistoryPage(kind, index = this.historyPages[kind].index) {
        const identity = token;
        const page = this.historyPages[kind];
        if (!identity || page.loading) return;
        const current = () => token === identity && this.historyPages[kind] === page;
        page.loading = true;
        try {
          while (true) {
            const rows = await request(`${historyPaths[kind]}?take=${pageSize + 1}&skip=${pageSize * index}`);
            if (!current()) return;
            if (rows.length || index === 0) {
              this[kind] = rows.slice(0, pageSize);
              page.index = index;
              page.more = rows.length > pageSize;
              return;
            }
            index--;
          }
        } catch (error) {
          if (!current()) return;
          if (error.status === 401) this.expire(feedback(error));
          else this.lobbyError = feedback(error);
        } finally {
          page.loading = false;
        }
      },

      matchDate(match) {
        return new Date(match.finishedAt).toLocaleString(i18n.locales[this.lang], {dateStyle: 'long', timeStyle: 'short'});
      },

      matchRival(match) {
        const rival = match.defender?.id === this.user?.id ? match.attacker : match.defender;
        return rival?.nickname || this.t('guestLabel');
      },

      replayHref(id) { return `?replay=${encodeURIComponent(id)}`; },
      canContinue(duel) { return ['waiting', 'playing'].includes(String(duel.status).toLowerCase()); },

      async refreshOnline() {
        const identity = token;
        if (!identity) return;
        const query = new URLSearchParams();
        if (this.onlineSample !== null) {
          query.set('sample', 'false');
          for (const id of this.onlineSample) query.append('ids', id);
        }
        try {
          const {count, players} = await request(`/api/users/online?${query}`);
          if (identity !== token) return;
          this.onlineCount = count;
          this.onlineSample ??= players.map(player => player.id);
          const roster = new Map(players.map(player => [player.id, player]));
          this.onlinePlayers = this.onlineSample.map(id => roster.get(id)).filter(Boolean);
        } catch {
          if (identity !== token) return;
          this.onlineCount = null;
          this.onlinePlayers = [];
        }
      },

      knowsPlayer(id) {
        return this.friends.some(friend => friend.id === id) ||
          this.outgoing.some(request => request.to?.id === id) || this.incoming.some(request => request.from?.id === id);
      },

      async addFriend(fromList) {
        const nickname = (fromList ?? this.friendNick).trim();
        if (this.busy || !nickname) return;
        this.busy = true;
        this.lobbyError = '';
        this.note = '';
        try {
          await request('/api/friends/requests', { method: 'POST', body: { nickname } });
          if (fromList == null) this.friendNick = '';
          this.note = message('requestSent', { name: nickname });
          await this.refreshLobby();
        } catch (error) {
          this.lobbyError = feedback(error);
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
          this.lobbyError = feedback(error);
        } finally {
          this.busy = false;
        }
      },

      async removeFriend(friend) {
        if (!confirm(i18n.t('removeFriend', { name: friend.nickname }))) return;
        this.busy = true;
        this.lobbyError = '';
        try {
          await request(`/api/friends/${encodeURIComponent(friend.id)}`, { method: 'DELETE' });
          this.friends = this.friends.filter(row => row.id !== friend.id);
        } catch (error) {
          this.lobbyError = feedback(error);
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
          this.lobbyError = feedback(error);
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
          this.lobbyError = feedback(error);
        }
      },

      joinRandom() {
        if (search) {
          this.searching = false;
          clearTimeout(search.retry);
          clearInterval(search.clock);
          if (!search.pending) this.endSearch(search);
          return;
        }
        if (this.busy) return;
        const job = search = {start: performance.now(), identity: token};
        this.searching = this.searchLocked = this.busy = true;
        document.querySelector('header').inert = true;
        this.searchTime = '00:00';
        this.lobbyError = '';
        job.clock = setInterval(() => {
          const elapsed = Math.floor((performance.now() - job.start) / 1000);
          this.searchTime = [Math.floor(elapsed / 60), elapsed % 60].map(n => String(n).padStart(2, '0')).join(':');
        }, 250);
        const attempt = async () => {
          job.pending = true;
          try {
            const answer = await request('/api/duels/random/join', {method: 'POST'});
            if (search !== job) return;
            if (answer) {
              this.endSearch(job);
              if (token === job.identity) this.openBoard(answer.id, answer.seatToken ?? null);
              return;
            }
          } catch (error) {
            if (search !== job) return;
            if (this.searching) this.lobbyError = feedback(error);
            this.endSearch(job);
            return;
          } finally {
            job.pending = false;
          }
          if (search !== job) return;
          if (this.searching) job.retry = setTimeout(attempt, 2000);
          else this.endSearch(job);
        };
        attempt();
      },

      endSearch(job) {
        if (search !== job) return;
        clearInterval(job.clock);
        clearTimeout(job.retry);
        search = null;
        this.searching = this.searchLocked = this.busy = false;
        document.querySelector('header').inert = false;
      },

      async createDuel(friendId) {
        if (this.busy) return;
        this.busy = true;
        this.lobbyError = '';
        this.note = '';
        try {
          const answer = await request('/api/duels', { method: 'POST',
            body: { side: this.side, ...(!friendId && this.randomOpponent ? {randomOpponent: true} : {}) } });
          remember(answer.id, answer.seatToken);
          if (friendId) {
            try {
              await request('/api/invites', { method: 'POST', body: { friendId, roomId: answer.id } });
            } catch (error) {
              this.note = message('inviteFailed', { error: feedback(error) });
            }
          }
          this.openBoard(answer.id, answer.seatToken ?? null);
        } catch (error) {
          this.lobbyError = feedback(error);
        } finally {
          this.busy = false;
        }
      },

      inviteFriend(friend) {
        return this.createDuel(friend.id);
      },

      async continueDuel(duel) {
        const status = String(duel.status).toLowerCase();
        if (['finished', 'cancelled', 'abandoned', 'interrupted'].includes(status)) {
          this.lobbyError = message('duelClosed');
          return;
        }
        this.openBoard(duel.id, seatOf(duel.id));
      },

      async cancelDuel(duel) {
        if (!confirm(i18n.t('closeDuel'))) return;
        this.busy = true;
        this.lobbyError = '';
        try {
          await request(`/api/duels/${encodeURIComponent(duel.id)}`, { method: 'DELETE' });
          forgetSeat(duel.id);
          await this.refreshDuels();
        } catch (error) {
          this.lobbyError = feedback(error);
        } finally {
          this.busy = false;
        }
      },

      async copyLink(roomId) {
        const text = linkFor(roomId);
        try {
          await navigator.clipboard.writeText(text);
          this.note = message('linkCopied');
        } catch {
          this.note = text;
        }
      },

      closedReason(duel) {
        const code = String(duel.status).toLowerCase();
        if (code === 'interrupted') return this.t('interruptedDuel');
        if (code === 'abandoned') return this.t('idleDuel');
        return '';
      },

      statusOf(status) {
        const code = String(status).toLowerCase();
        return duelStatuses.has(code) ? this.t(`duel.${code}`) : code;
      },
      sideOf(side) {
        return this.t(String(side).toLowerCase() === 'attacker' ? 'attacker' : 'defender');
      },

      // ---- hub payloads ----
      pushRequest(payload) {
        const row = payload?.request;
        if (!row?.id || this.incoming.some(item => item.id === row.id)) return;
        this.incoming = [row, ...this.incoming];
        this.note = message('requestFrom', { name: row.from?.nickname ?? message('player') });
      },

      pushInvite(payload) {
        const row = payload?.invite;
        if (!row?.id || this.invites.some(item => item.id === row.id)) return;
        this.invites = [row, ...this.invites];
        sounds.play('duelInvite');
        this.note = message('invitedBy', { name: row.from?.nickname ?? message('opponentName') });
      },

      resolveInvite(payload) {
        if (!payload?.id) return;
        this.invites = this.invites.filter(row => row.id !== payload.id);
        if (payload.status === 'accepted' || payload.status === 'Accepted') {
          this.note = message('inviteAccepted');
        } else if (payload.roomClosed && this.screen === 'lobby') {
          this.note = message('inviteDeclined');
          this.refreshDuels();
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

      expire(reason) {
        if (search) this.endSearch(search);
        forgetExpiredToken();
        this.user = null;
        this.error = reason || message('expired');
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
        if (!id) { this[slot] = message('missingRoom'); return; }
        // A chair answers to the seat token or to the account, so an old seat opens without joining
        if (await this.canOpen(id)) {
          this.openBoard(id, seatOf(id));
          return;
        }
        try {
          const held = !token && store.get(key.guestRoom);
          const answer = await request(`/api/duels/${encodeURIComponent(id)}/join`,
            { method: 'POST', seat: held ? seatOf(held) : null });
          remember(id, answer?.seatToken);
          this.openBoard(id, answer?.seatToken ?? null);
        } catch (error) {
          this[slot] = feedback(error);
        }
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

  function forgetExpiredToken() {
    const current = sharedToken();
    if (current && current !== token) { location.reload(); return; }
    token = null;
    persistToken(null);
  }

  async function boot() {
    const params = new URLSearchParams(location.search);
    const recording = params.get('replay');
    if (recording !== null) {
      live.screen = 'replay';
      tell('replay', {id: recording});
      return;
    }
    const room = params.get('room');
    if (room) live.pendingRoom = room;
    if (token) {
      try {
        live.user = await request('/api/auth/me');
      } catch (error) {
        if (error.status === 401 || error.status === 403) forgetExpiredToken();
        else {
          live.sessionCheckFailed = true;
          live.error = feedback(error);
          live.screen = 'auth';
          return;
        }
      }
    }
    if (token && live.user) {
      await live.enterLobby();
      return;
    }
    token = null;
    // A reload in the same tab keeps the guest seat.
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
      if (detail.message?.key === 'inviteDeclined') { live.note = detail.message; live.lobbyError = ''; }
      else if (detail.message) live.lobbyError = detail.message;
      live.refreshLobby();
      live.refreshDuels();
      live.refreshOnline();
      live.refreshMatches();
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
    unwatchRoom,
    seatOf,
    remember,
    forgetSeat,
    createGuestRoom,
    linkFor,
    connected: () => hub?.state === 'Connected',
    get token() { return token; },
  };
})();
