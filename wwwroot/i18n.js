const i18n = (() => {
  const strings = {
    homeLink: ['Truck-kun Tower — на главный экран', 'Truck-kun Tower — home'],
    github: ['Исходный код на GitHub', 'Source on GitHub'],
    language: ['Язык', 'Language'],
    theme: ['Тема: {mode}', 'Theme: {mode}'],
    'theme.system': ['система', 'system'],
    'theme.light': ['светлая', 'light'],
    'theme.dark': ['тёмная', 'dark'],
    sound: ['Звук', 'Sound'],
    soundUnavailable: ['Звук недоступен', 'Sound unavailable'],
    soundOn: ['Звук вкл', 'Sound on'],
    soundOff: ['Звук выкл', 'Sound off'],

    authTitle: ['Меня сбил грузовик, и я переродился башней в мире слизней', 'A truck hit me, and I woke up as a tower in a world of slimes'],
    lede: ['Пять волн на двоих. Укрепляй свою дорогу или отправляй слизней на чужую', 'Five waves, two players. Defend the road or send slimes down it'],
    kicker: ['Исекай · тактическая дуэль · io-game', 'Isekai · tactical duel · io-game'],
    authTabs: ['Вход или регистрация', 'Sign in or register'],
    login: ['Вход', 'Sign in'],
    register: ['Регистрация', 'Register'],
    nickname: ['Ник', 'Nickname'],
    password: ['Пароль', 'Password'],
    passwordRule: ['от 8 до 72 символов', '8 to 72 characters'],
    busy: ['Секунду…', 'Please wait…'],
    signIn: ['Войти', 'Sign in'],
    signUp: ['Зарегистрироваться', 'Register'],
    or: ['или', 'or'],
    join: ['Вступить в дуэль', 'Join the duel'],
    guest: ['Играть гостем', 'Play as guest'],
    pendingRoom: ['Ссылка ведёт в комнату {room}. Аккаунт не обязателен', 'This link opens room {room}. No account required'],
    side: ['Сторона', 'Side'],
    defender: ['Защитник', 'Defender'],
    attacker: ['Атакующий', 'Attacker'],
    fiveWaves: ['5 волн', '5 waves'],
    mode: ['tower defence', 'tower defence'],
    shortName: ['Ник короче трёх символов', 'Use at least three characters for your nickname'],
    shortPassword: ['Пароль короче восьми символов', 'Use at least eight characters for your password'],
    longPassword: ['Пароль длиннее 72 символов', 'Use no more than 72 characters for your password'],
    expired: ['Сессия больше не действует, войди заново', 'Your session expired. Sign in again'],

    lobby: ['Лобби', 'Lobby'],
    profile: ['Профиль', 'Profile'],
    joinedSince: ['Аккаунт с', 'Member since'],
    logout: ['Выйти', 'Sign out'],
    friends: ['Друзья', 'Friends'],
    addFriend: ['Добавить в друзья по нику', 'Add a friend by nickname'],
    add: ['Добавить', 'Add'],
    online: ['в сети', 'online'],
    offline: ['не в сети', 'offline'],
    inviteFriend: ['Пригласить в дуэль', 'Invite to a duel'],
    remove: ['Удалить', 'Remove'],
    emptyFriends: ['Пока никого. Добавь друга по нику — и приглашай его в дуэль', 'No friends yet. Add a friend by nickname to invite them to a duel'],
    friendRequests: ['Заявки в друзья', 'Friend requests'],
    accept: ['Принять', 'Accept'],
    decline: ['Отклонить', 'Decline'],
    awaitingAnswer: ['ждём ответа', 'awaiting reply'],
    cancel: ['Отменить', 'Cancel'],
    rematchShort: ['Играть снова', 'Play again'],
    afterMatch: ['Что дальше', 'What next'],
    built: ['Постройки этого хода', 'Built this turn'],
    invitations: ['Приглашения в дуэль', 'Duel invitations'],
    invitedBy: ['{name} зовёт в дуэль', '{name} invites you to a duel'],
    newDuel: ['Новая дуэль', 'New duel'],
    createDuel: ['Создать дуэль', 'Create a duel'],
    createHint: ['После создания пришли другу ссылку-приглашение или нажми «Пригласить в дуэль» рядом с его ником', 'Send the invite link to a friend, or choose “Invite to a duel” next to their nickname'],
    myDuels: ['Мои дуэли', 'My duels'],
    emptySeat: ['свободное место', 'open seat'],
    continue: ['Продолжить', 'Continue'],
    link: ['Ссылка', 'Link'],
    tabNote: ['Одна вкладка — одно место в комнате. Ссылка-приглашение работает и для гостя', 'Each tab holds one seat. Guests can use the invite link'],
    'duel.waiting': ['ждём соперника', 'waiting for opponent'],
    'duel.playing': ['в игре', 'in progress'],
    'duel.finished': ['завершена', 'finished'],
    'duel.cancelled': ['отменена', 'cancelled'],
    'duel.abandoned': ['брошена', 'abandoned'],
    requestSent: ['Заявка отправлена: {name}', 'Friend request sent to {name}'],
    removeFriend: ['Удалить {name} из друзей?', 'Remove {name} from your friends?'],
    inviteFailed: ['Дуэль создана, но приглашение не ушло: {error}', 'The duel is ready, but the invite failed: {error}'],
    duelClosed: ['Эта дуэль уже закрыта', 'This duel is closed'],
    closeDuel: ['Закрыть комнату? Соперник её больше не откроет.', 'Close the room? Your opponent cannot reopen it.'],
    requestFrom: ['Заявка в друзья от {name}', 'Friend request from {name}'],
    player: ['игрока', 'a player'],
    opponentName: ['Соперник', 'An opponent'],
    inviteAccepted: ['Приглашение приняли — соперник уже в комнате', 'Invite accepted. Your opponent is in the room'],
    missingRoom: ['Ссылка без комнаты', 'The link has no room ID'],
    joinFailed: ['Не удалось войти в комнату', 'Could not join the room'],

    backToDuels: ['К списку дуэлей', 'Back to duels'],
    backHome: ['На главную', 'Back to home'],
    inviteOpponent: ['Пригласить соперника', 'Invite an opponent'],
    inviteLink: ['Ссылка для второго игрока', 'Link for the second player'],
    copyLink: ['Скопировать ссылку', 'Copy link'],
    linkCopied: ['Ссылка скопирована', 'Link copied'],
    selectLink: ['Скопируй выделенную ссылку', 'Copy the selected link'],
    fieldLog: ['Полевой дневник', 'Field log'],
    keep: ['Крепость', 'Keep'],
    gold: ['Золото', 'Gold'],
    goldUnit: ['зол.', 'gold'],
    boardCells: ['Клетки поля', 'Board cells'],
    waveOrders: ['Приказы на волну', 'Wave orders'],
    orderType: ['Тип приказа', 'Order type'],
    groups: ['Группы на эту волну', 'Groups for this wave'],
    ready: ['Готов к волне', 'Ready for the wave'],
    readyConfirmed: ['Готовность подтверждена', 'Ready'],
    skipWave: ['Пропустить волну', 'Skip wave'],
    swap: ['Поменяться сторонами', 'Swap sides'],
    swapWait: ['Ждём ответа соперника', 'Waiting for your opponent’s reply'],
    swapAsk: ['Соперник предлагает поменяться сторонами', 'Your opponent wants to swap sides'],
    rematch: ['Играть снова', 'Play again'],
    again: ['Новая дуэль', 'New duel'],
    loading: ['Загружаем поле', 'Loading the board'],
    missingBoard: ['Сервер не прислал поле дуэли', 'The server did not send a board'],
    battle: ['Волна {wave} из {total} · бой', 'Wave {wave} of {total} · battle'],
    planning: ['Волна {wave} из {total} · подготовка', 'Wave {wave} of {total} · planning'],
    result: ['{outcome} · {keep}', '{outcome} · {keep}'],
    win: ['Победа', 'Victory'],
    loss: ['Поражение', 'Defeat'],
    keepStanding: ['крепость выстояла', 'the keep held'],
    keepFell: ['крепость пала', 'the keep fell'],
    waitingPlayer: ['Ждём второго игрока', 'Waiting for the second player'],
    waitingReady: ['Ты готов. Ждём соперника', 'You’re ready. Waiting for your opponent'],
    noOpponent: ['Второй игрок ещё не подключился — пришли ему ссылку-приглашение', 'No opponent yet. Send someone the invite link'],
    opponent: ['Соперник: {name}', 'Opponent: {name}'],
    guestOpponent: ['Соперник: гость без аккаунта', 'Opponent: guest'],
    timeLeft: ['на размышление {seconds} с', '{seconds} s to plan'],
    timeUp: ['время вышло', 'time’s up'],
    gate: ['Вход {index}', 'Gate {index}'],
    wallCell: ['Стена: столбец {x}, ряд {y}', 'Wall: column {x}, row {y}'],
    roadCell: ['Тропа: столбец {x}, ряд {y}', 'Path: column {x}, row {y}'],
    chooseRoad: ['Выбери отмеченную клетку на тропе', 'Choose a marked cell on the path'],
    chooseWall: ['Выбери стену у тропы или перейди на забор и ловушки', 'Choose a wall by the path, or switch to fences or traps'],
    chooseGate: ['Выбери вход, чтобы увидеть маршруты', 'Choose a gate to preview the routes'],
    selectedCell: ['Выбрана клетка: {x}, {y}', 'Selected cell: {x}, {y}'],
    selectedGate: ['{gate} · {route}', '{gate} · {route}'],
    shortestRoute: ['кратчайший путь', 'shortest route'],
    randomRoute: ['выбор ветки 50/50', 'equal chance at each fork'],
    build: ['Построить', 'Build'],
    addGroup: ['Добавить в волну', 'Add to the wave'],
    chooseBuilding: ['Выбери постройку', 'Choose a structure'],
    chooseGroup: ['Выбери группу', 'Choose a group'],
    'action.bolt': ['Арбалет', 'Crossbow'],
    'hint.bolt': ['Сильный удар по одной цели', 'Hits one target hard'],
    'action.pulse': ['Руна', 'Rune'],
    'hint.pulse': ['Бьёт всех в радиусе', 'Hits all targets in range'],
    'action.swarm': ['Стайка', 'Swarm'],
    'hint.swarm': ['Пять мелких слизней', 'Five small slimes'],
    'action.giant': ['Гигант', 'Giant'],
    'hint.giant': ['Один живучий слизень', 'One tough slime'],
    'action.smart': ['Умные слизни', 'Smart slimes'],
    'hint.smart': ['Пять в очках идут кратчайшим путём', 'Five slimes in glasses take the shortest route'],
    'action.runner': ['Бегуны', 'Runners'],
    'hint.runner': ['Шесть быстрых, но хрупких', 'Six fast but fragile slimes'],
    'action.armored': ['Бронированные', 'Armored slimes'],
    'hint.armored': ['Три медленных, держат удар', 'Three slow but tough slimes'],
    'action.ranged': ['Дальнобойные', 'Ranged slimes'],
    'hint.ranged': ['Два слизня. Останавливаются и стреляют в зоне 5×5, до 10 выстрелов каждый', 'Two slimes stop and fire within a 5×5 area. Each fires up to 10 shots'],
    'action.apc': ['Бронетранспортёр', 'Armored carrier'],
    'hint.apc': ['Высадит троих по очереди после 70% пути или при встрече с забором', 'Drops off three slimes in sequence after 70% of the route, or when it reaches a fence'],
    'action.fence': ['Забор', 'Fence'],
    'hint.fence': ['Слизни остановятся, чтобы его сломать', 'Slimes stop to break it'],
    'action.cannon': ['Пушка', 'Cannon'],
    'hint.cannon': ['Медленно бьёт, зато больно', 'Fires slowly but hits hard'],
    'action.frost': ['Мороз', 'Frost'],
    'hint.frost': ['Замедляет всех в радиусе', 'Slows all targets in range'],
    'action.spikes': ['Шипы', 'Spikes'],
    'hint.spikes': ['Ловушка с ограниченным запасом ударов. БТР проходит без урона', 'Has a limited number of hits. Carriers take no damage'],
    'action.mine': ['Мина', 'Mine'],
    'hint.mine': ['Убирает первого наступившего врага и исчезает', 'Removes the first enemy that steps on it, then disappears'],
    trapLimit: ['Одна ловушка на маршрут', 'One per route'],
    limitUsage: ['{used}/{max}', '{used}/{max}'],
    purchaseLimit: ['Достигнут лимит покупок этого типа', 'Purchase limit reached for this type'],

    errorHeading: ['Ошибка', 'Error'],
    unexpected: ['Что-то пошло не так', 'Something went wrong'],
    'http.0': ['Сеть недоступна, проверь соединение', 'Network unavailable. Check your connection'],
    'http.400': ['Проверь введённые данные', 'Check your input'],
    'http.401': ['Нужен вход', 'Sign in to continue'],
    'http.403': ['Нет доступа', 'Access denied'],
    'http.404': ['Не найдено', 'Not found'],
    'http.409': ['Действие недоступно', 'Action unavailable'],
    'http.429': ['Слишком много попыток, подожди немного', 'Too many attempts. Try again later'],
    requestFailed: ['Запрос не прошёл: {status}', 'Request failed: {status}'],
    apiDetail: ['{summary} — {detail}', '{summary} — {detail}'],
  };

  const locales = { ru: 'ru-RU', en: 'en-US' };
  const key = 'truck-kun-tower:lang';
  let lang = 'ru';
  try { if (localStorage.getItem(key) === 'en') lang = 'en'; } catch {}
  const message = (key, params) => ({ key, params });
  const text = (value, language = lang) => typeof value === 'object' && value !== null
    ? t(value.key, value.params, language) : String(value ?? '');
  const t = (key, params = {}, language = lang) => (strings[key]?.[language === 'en' ? 1 : 0] ?? key)
    .replace(/\{(\w+)\}/g, (_, name) => text(params[name], language));
  const feedback = error => error?.feedback ?? message('unexpected');
  const onChange = listener => window.addEventListener('tower:language', () => listener(lang));

  const buttons = document.querySelectorAll('[data-language]');
  const attributes = ['aria-label', 'title'];
  function paint() {
    document.documentElement.lang = lang;
    for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.dataset.i18n);
    for (const attr of attributes) {
      const source = `data-i18n-${attr}`;
      for (const node of document.querySelectorAll(`[${source}]`)) node.setAttribute(attr, t(node.getAttribute(source)));
    }
    for (const button of buttons) button.setAttribute('aria-pressed', String(button.dataset.language === lang));
  }
  for (const button of buttons) button.addEventListener('click', () => {
    const next = button.dataset.language;
    if (next === lang || !Object.hasOwn(locales, next)) return;
    lang = next;
    try { localStorage.setItem(key, lang); } catch {}
    paint();
    window.dispatchEvent(new Event('tower:language'));
  });
  paint();

  return { t, text, message, feedback, onChange, locales, get lang() { return lang; },
    http(status, detail, code) {
      if (status === 409 && code === 'purchase_limit') return message('purchaseLimit');
      const name = `http.${status}`;
      const summary = Object.hasOwn(strings, name) ? message(name) : message('requestFailed', { status });
      return detail ? message('apiDetail', { summary, detail }) : summary;
    },
  };
})();
