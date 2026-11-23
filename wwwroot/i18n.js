const i18n = (() => {
  const strings = {
    homeLink: ['Truck-kun Tower — на главный экран', 'Truck-kun Tower — home'],
    github: ['Исходный код на GitHub', 'Source on GitHub'],
    language: ['Язык', 'Language'],
    theme: ['Тема: {mode}', 'Theme: {mode}'],
    'theme.system': ['система', 'system'],
    'theme.light': ['светлая', 'light'],
    'theme.dark': ['тёмная', 'dark'],
    guide: ['Справка', 'Guide'],
    guideClose: ['Закрыть', 'Close'],
    guideUnavailable: ['Не удалось загрузить характеристики. Закрой справку и попробуй ещё раз', 'Could not load stats. Close the guide and try again'],
    guideUnits: ['Юниты', 'Units'],
    guideStructures: ['Постройки', 'Structures'],
    guideBudgets: ['Монеты по волнам', 'Coins by wave'],
    guideWave: ['Волна', 'Wave'],
    guideStructureBonus: ['Бонус за постройку', 'Bonus per structure'],
    guideEconomy: ['В начале волны обе стороны получают базовый бюджет из таблицы. Неистраченные монеты не переносятся. Бонус получает только атакующий: за каждую башню, забор и ловушку, которые остались после предыдущего боя. Крепость не считается. Отмена заказа или снос постройки, купленной в текущей волне, возвращает монеты. За снос старых построек монеты не возвращаются', 'Both sides get the base budget in the table at the start of each wave. Unspent coins do not carry over. Only the attacker gets a bonus for each tower, fence, and trap that survived the previous battle. The keep does not count. Cancel an order or remove a structure bought this wave to get its coins back. Old structures give no refund'],
    guideHp: ['HP', 'HP'],
    guideDamage: ['Урон', 'Damage'],
    guideRadius: ['Радиус, кл.', 'Range, tiles'],
    guideSpeed: ['Тиков на клетку', 'Ticks per tile'],
    guideSpeedNote: ['Чем меньше тиков на клетку, тем быстрее юнит. Указана скорость без препятствий и замедления', 'Fewer ticks per tile means faster movement. The values apply without obstacles or slow effects'],
    guideInterval: ['Интервал, тики', 'Interval, ticks'],
    guideNotes: ['HP указано на одного юнита, от 1-й до {total}-й волны. Интервал — число тиков между атаками или замедлениями; 0 означает, что периодической атаки нет. Радиус считается по оси, у больших зон углы скруглены', 'HP is per unit, from wave 1 to wave {total}. Interval is the ticks between attacks or slows. A 0 means no periodic attack. Range is along each axis. Large areas have cut corners'],
    'guide.unitDamage': ['{keep} крепости / {fence} забору', '{keep} to keep / {fence} to fence'],
    'guide.armored': ['{structure} забору и башне / {keep} крепости. Бьёт башню с соседней клетки, не по диагонали. После её разрушения продолжает путь', '{structure} to fences and towers / {keep} to the keep. Attacks towers from an adjacent tile, not diagonally. Surviving slimes then continue along the route'],
    'guide.ranged': ['{structure} постройке / {keep} крепости', '{structure} to structures / {keep} to keep'],
    'guide.apc': ['Ломает забор и высаживает пассажиров', 'Breaks a fence and drops off passengers'],
    'guide.mine': ['Убивает при срабатывании', 'Kills on contact'],
    'guide.frost': ['0, замедляет', '0, slows'],
    'guide.pulse': ['{damage} по максимум {targets} целям', '{damage} to up to {targets} targets'],
    'action.balaclava': ['Пассажир БТР', 'Carrier passenger'],
    'action.keep': ['Крепость', 'Keep'],
    sound: ['Звук', 'Sound'],
    soundUnavailable: ['Звук недоступен', 'Sound unavailable'],
    soundWaiting: ['Звук включён. Воспроизведение начнётся после взаимодействия со страницей', 'Sound is on. Interact with the page to allow playback'],
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
    logout: ['Выйти из аккаунта', 'Sign out'],
    retrySession: ['Повторить проверку входа', 'Retry session check'],
    friends: ['Друзья', 'Friends'],
    addFriend: ['Добавить в друзья по нику', 'Add a friend by nickname'],
    add: ['Добавить', 'Add'],
    online: ['в сети', 'online'],
    offline: ['не в сети', 'offline'],
    onlineCount: ['В сети: {count}', 'Online: {count}'],
    onlineEmpty: ['В списке нет игроков. Обнови страницу, чтобы выбрать новый список', 'No players in this list. Reload the page for a new list'],
    onlineAddFriend: ['Отправить заявку в друзья', 'Send friend request'],
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
    randomOpponent: ['Случайный противник', 'Random opponent'],
    randomCreateHint: ['К этой комнате можно подключиться кнопкой случайной дуэли', 'Players can join this room from the random duel button'],
    joinRandom: ['Подключиться к случайной дуэли', 'Join a random duel'],
    opponentLeftDuel: ['Дуэль прервана: соперник вышел', 'Duel ended: your opponent left'],
    idleDuel: ['Дуэль прервана: один из игроков отключился или не сделал ход', 'Duel ended: a player disconnected or did not act'],
    stopRandom: ['Выйти из подбора', 'Cancel search'],
    searchTime: ['Время ожидания: {elapsed}', 'Wait time: {elapsed}'],
    newDuel: ['Новая дуэль', 'New duel'],
    createDuel: ['Создать дуэль', 'Create a duel'],
    createHint: ['После создания пришли другу ссылку-приглашение или нажми «Пригласить в дуэль» рядом с его ником', 'Send the invite link to a friend, or choose “Invite to a duel” next to their nickname'],
    myDuels: ['Мои дуэли', 'My duels'],
    emptySeat: ['свободное место', 'open seat'],
    continue: ['Продолжить', 'Continue'],
    link: ['Ссылка', 'Link'],
    'duel.waiting': ['ждём соперника', 'waiting for opponent'],
    'duel.playing': ['в игре', 'in progress'],
    'duel.finished': ['завершена', 'finished'],
    'duel.cancelled': ['отменена', 'cancelled'],
    'duel.abandoned': ['брошена', 'abandoned'],
    'duel.interrupted': ['прервана', 'interrupted'],
    interruptedDuel: ['Сервер перезапустился, и доска не сохранилась. Начни новую дуэль', 'The server restarted, so the board was not kept. Start a new duel'],
    requestSent: ['Заявка отправлена: {name}', 'Friend request sent to {name}'],
    removeFriend: ['Удалить {name} из друзей?', 'Remove {name} from your friends?'],
    inviteFailed: ['Дуэль создана, но приглашение не ушло: {error}', 'The duel is ready, but the invite failed: {error}'],
    duelClosed: ['Эта дуэль уже закрыта', 'This duel is closed'],
    closeDuel: ['Закрыть комнату? Соперник её больше не откроет.', 'Close the room? Your opponent cannot reopen it.'],
    requestFrom: ['Заявка в друзья от {name}', 'Friend request from {name}'],
    player: ['игрока', 'a player'],
    opponentName: ['Соперник', 'An opponent'],
    inviteDeclined: ['Соперник отказался от дуэли', 'Your opponent declined the duel'],
    inviteAccepted: ['Приглашение приняли — соперник уже в комнате', 'Invite accepted. Your opponent is in the room'],
    missingRoom: ['Ссылка без комнаты', 'The link has no room ID'],

    leaveDuel: ['Выйти', 'Leave'],
    demolish: ['Снести', 'Demolish'],
    noRefund: ['За снос старой постройки золото не возвращается', 'Old structures give no refund'],
    duelBusy: ['Сначала выйди из текущей дуэли', 'Leave your current duel first'],
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
    boardRow: ['Ряд {index}', 'Row {index}'],
    boardColumn: ['Столбец {index}', 'Column {index}'],
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
    replayLink: ['Повтор', 'Replay'],
    previousPage: ['Назад', 'Previous'],
    nextPage: ['Вперёд', 'Next'],
    pageNumber: ['Страница {page}', 'Page {page}'],
    matchHistory: ['Журнал матчей', 'Match history'],
    guestLabel: ['Гость', 'Guest'],
    watchReplay: ['Смотреть реплей матча', 'Watch match replay'],
    archiveWave: ['Реплей: волна {wave} из {total}', 'Replay: wave {wave} of {total}'],
    chooseReplayWave: ['Выбрать волну', 'Choose a wave'],
    replayAgain: ['Сначала эту волну', 'Restart this wave'],
    replayUnavailable: ['Запись матча недоступна', 'Match replay is not available'],
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
    chooseRoad: ['Нажми на отмеченную клетку, чтобы поставить забор или ловушку', 'Click a marked path cell to place a fence or trap'],
    chooseWall: ['Нажми на клетку у тропы, чтобы построить башню', 'Click a cell by the path to build a tower'],
    chooseGate: ['Нажми на вход, чтобы добавить туда группу', 'Click a gate to deploy the group there'],
    selectedGate: ['{gate} · {route}', '{gate} · {route}'],
    shortestRoute: ['кратчайший путь', 'shortest route'],
    randomRoute: ['случайный маршрут', 'random route'],
    chooseBuilding: ['Выбери постройку', 'Choose a structure'],
    'action.bolt': ['Арбалет', 'Crossbow'],
    'hint.bolt': ['Сильный удар по одной цели', 'Hits one target hard'],
    'action.pulse': ['Руна', 'Rune'],
    'hint.pulse': ['Бьёт ближайших к себе врагов в радиусе', 'Hits the enemies closest to the rune within range'],
    'action.swarm': ['Стайка', 'Swarm'],
    'hint.swarm': ['Пять мелких слизней', 'Five small slimes'],
    'action.giant': ['Гигант', 'Giant'],
    'hint.giant': ['Один живучий слизень', 'One tough slime'],
    'action.smart': ['Умные слизни', 'Smart slimes'],
    'hint.smart': ['Пять в очках идут кратчайшим путём', 'Five slimes in glasses take the shortest route'],
    'action.runner': ['Бегуны', 'Runners'],
    'hint.runner': ['Шесть быстрых, но хрупких', 'Six fast but fragile slimes'],
    'action.armored': ['Бронированные', 'Armored slimes'],
    'hint.armored': ['Три стойких слизня, останавливаются у башен и бьют их вблизи', 'Three tough slimes stop to attack adjacent towers'],
    'action.ranged': ['Дальнобойные', 'Ranged slimes'],
    'hint.ranged': ['Два слизня. Останавливаются и стреляют в зоне 5×5, до 10 выстрелов каждый', 'Two slimes stop and fire within a 5×5 area. Each fires up to 10 shots'],
    'action.apc': ['Бронетранспортёр', 'Armored carrier'],
    'hint.apc': ['Высадит пятерых по очереди после 70% пути или при встрече с забором', 'Drops off five slimes in sequence after 70% of the route, or when it reaches a fence'],
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
    inviteSenderOffline: ['Отправитель приглашения не в сети. Попробуй, когда он вернётся',
      'The sender is offline. Try again when they return'],
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
  function initialLanguage() {
    try {
      const saved = localStorage.getItem(key);
      if (Object.hasOwn(locales, saved)) return saved;
    } catch {}
    for (const tag of [...(navigator.languages ?? []), navigator.language ?? '']) {
      const primary = tag.toLowerCase().split('-')[0];
      if (Object.hasOwn(locales, primary)) return primary;
    }
    return 'en';
  }
  let lang = initialLanguage();
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
    if (!Object.hasOwn(locales, next)) return;
    try { localStorage.setItem(key, next); } catch {}
    if (next === lang) return;
    lang = next;
    paint();
    window.dispatchEvent(new Event('tower:language'));
  });
  paint();

  return { t, text, message, feedback, onChange, locales, get lang() { return lang; },
    http(status, detail, code) {
      if (status === 404 && code === 'room_abandoned') return message('idleDuel');
      if (status === 404 && code === 'room_cancelled') return message('opponentLeftDuel');
      if (status === 409 && code === 'invite_sender_offline') return message('inviteSenderOffline');
      if (status === 409 && code === 'duel_busy') return message('duelBusy');
      if (status === 409 && code === 'purchase_limit') return message('purchaseLimit');
      const name = `http.${status}`;
      const summary = Object.hasOwn(strings, name) ? message(name) : message('requestFailed', { status });
      return detail ? message('apiDetail', { summary, detail }) : summary;
    },
  };
})();
