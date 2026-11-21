const sounds = (() => {
  const Audio = window.AudioContext || window.webkitAudioContext;
  const key = 'truck-kun-tower:sound';
  const button = document.getElementById('sound');
  let enabled = true, audio, output;
  try { enabled = localStorage.getItem(key) !== 'off'; } catch {}
  const recent = new Map();
  const cues = {
    coin: [[0, 880, 1100, .08, 'sine', .12], [.065, 1320, 1568, .15, 'sine', .10]],
    slime: [[0, 170, 420, .075, 'triangle', .12], [.06, 360, 95, .16, 'sine', .14]],
    bolt: [[0, 530, 120, .055, 'triangle', .12], [.035, 240, 85, .075, 'square', .035]],
    rune: [[0, 330, 680, .12, 'triangle', .09], [.035, 495, 980, .14, 'sine', .08]],
    siege: [[0, 760, 230, .045, 'square', .055], [.018, 184, 72, .105, 'triangle', .15]],
    mine: [[0, 105, 33, .25, 'triangle', .19], [0, 220, 58, .095, 'square', .07], [.052, 58, 28, .26, 'sine', .13]],
    apcMove: [[0, 61, 74, .14, 'triangle', .11], [.018, 115, 78, .07, 'square', .025], [.09, 91, 58, .09, 'triangle', .045]],
    fence: [[0, 190, 65, .055, 'square', .065], [.045, 290, 80, .12, 'triangle', .11]],
    hit: [[0, 118, 48, .17, 'triangle', .15], [.05, 404, 145, .15, 'sine', .10]],
    win: [[0, 523, 523, .16, 'triangle', .12], [.12, 659, 659, .16, 'triangle', .12], [.24, 784, 1047, .30, 'triangle', .10]],
    loss: [[0, 392, 330, .18, 'triangle', .12], [.14, 294, 247, .18, 'triangle', .10], [.29, 196, 98, .29, 'triangle', .10]],
  };
  const running = () => enabled && audio?.state === 'running';
  const update = () => {
    const ready = running();
    const waiting = Audio && enabled && !ready;
    const label = i18n.t(!Audio ? 'soundUnavailable' : waiting ? 'soundEnable' : ready ? 'soundOn' : 'soundOff');
    button.disabled = !Audio;
    button.title = label;
    document.getElementById('sound-label').textContent = label;
    button.setAttribute('aria-label', i18n.t(waiting ? 'soundEnable' : 'sound'));
    button.setAttribute('aria-pressed', String(ready));
    button.querySelector('.sound-mark').dataset.state = !Audio ? 'unavailable' : ready ? 'on' : 'off';
  };
  i18n.onChange(update);
  function unlock() {
    if (!enabled || !Audio) return Promise.resolve();
    try {
      if (!audio) {
        audio = new Audio();
        output = audio.createGain();
        output.connect(audio.destination);
        audio.addEventListener('statechange', update);
      }
      output.gain.value = .45;
      return audio.state === 'running' ? Promise.resolve() : audio.resume().then(update, update);
    } catch { return Promise.resolve(); }
  }
  function play(name, interval = 0) {
    if (!running()) return;
    const now = audio.currentTime;
    if (now < (recent.get(name) ?? -Infinity) + interval) return;
    recent.set(name, now);
    for (const [delay, from, to, length, shape, volume] of cues[name]) {
      const start = now + delay;
      const oscillator = audio.createOscillator(), envelope = audio.createGain();
      oscillator.type = shape;
      oscillator.frequency.setValueAtTime(from, start);
      oscillator.frequency.exponentialRampToValueAtTime(to, start + length);
      envelope.gain.setValueAtTime(.001, start);
      envelope.gain.linearRampToValueAtTime(volume, start + .006);
      envelope.gain.exponentialRampToValueAtTime(.001, start + length);
      oscillator.connect(envelope).connect(output);
      oscillator.start(start);
      oscillator.stop(start + length);
    }
  }
  button.addEventListener('click', () => {
    if (enabled && !running()) {
      unlock().then(() => play('coin'));
      return;
    }
    enabled = !enabled;
    try { localStorage.setItem(key, enabled ? 'on' : 'off'); } catch {}
    update();
    if (enabled) unlock().then(() => play('coin'));
    else if (output) output.gain.value = 0;
  });
  document.addEventListener('click', event => {
    if (!event.target.closest('#sound') && audio?.state !== 'running') unlock();
  }, true);
  update();
  return {play, unlock};
})();
