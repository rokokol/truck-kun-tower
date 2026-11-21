(() => {
  const trigger = document.getElementById('open-guide');
  const dialog = document.getElementById('guide');
  const status = document.getElementById('guide-status');
  const body = document.getElementById('guide-stats');
  const {t} = i18n;
  let rules, loading = false;
  const element = (tag, text) => Object.assign(document.createElement(tag), {textContent: text});

  const svgNs = 'http://www.w3.org/2000/svg';
  function icon(type, className) {
    const svg = document.createElementNS(svgNs, 'svg');
    svg.setAttribute('class', className);
    svg.setAttribute('viewBox', '0 0 32 32');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS(svgNs, 'use');
    use.setAttribute('href', `actors.svg#${type}`);
    use.setAttribute('width', '32');
    use.setAttribute('height', '32');
    svg.append(use);
    return svg;
  }

  function damage(row) {
    if (row.type === 'ranged') return t('guide.ranged', {structure: row.damage, keep: row.keepDamage});
    if (['apc', 'mine', 'frost', 'pulse'].includes(row.type)) return t(`guide.${row.type}`, {damage: row.damage});
    if (row.keepDamage != null) return t('guide.unitDamage', {keep: row.keepDamage, fence: row.fenceDamage});
    return row.damage;
  }

  function table(title, rows) {
    const node = document.createElement('table');
    node.className = 'guide-table';
    node.append(element('caption', t(title)));
    const head = node.createTHead().insertRow();
    for (const [key, symbol] of [
      ['guideName'], ['guideHp', 'heart'], ['guideDamage', 'hit-spark'],
      ['guideRadius', 'attack-radius'], ['guideInterval', 'attack-interval'],
    ]) {
      const label = element('span', t(key));
      const column = Object.assign(document.createElement('th'), {scope: 'col'});
      if (symbol) column.append(icon(symbol, 'guide-icon'));
      column.append(label);
      head.append(column);
    }
    const section = node.createTBody();
    for (const row of rows) {
      const line = section.insertRow();
      const name = Object.assign(document.createElement('th'), {scope: 'row'});
      const portrait = element('span', '');
      portrait.className = 'ddlc-snap guide-portrait';
      portrait.append(icon(row.type, 'guide-sprite'));
      const entry = element('span', '');
      entry.className = 'guide-entry';
      entry.append(portrait, element('span', t(`action.${row.type}`)));
      name.append(entry);
      line.append(name);
      const hp = row.hpFirst === row.hpLast ? row.hpFirst : `${row.hpFirst}–${row.hpLast}`;
      for (const value of [hp, damage(row), row.radius, row.interval]) line.insertCell().textContent = value;
    }
    return node;
  }

  function paint() {
    status.hidden = Boolean(rules);
    status.textContent = rules ? '' : t(loading ? 'loading' : 'guideUnavailable');
    if (!rules) return;
    const notes = element('p', t('guideNotes', {total: rules.waveCount}));
    notes.className = 'fine';
    body.replaceChildren(table('guideUnits', rules.units), table('guideStructures', rules.structures), notes);
  }

  trigger.onclick = async () => {
    dialog.showModal();
    if (rules || loading) return;
    loading = true;
    paint();
    try {
      rules = await Tower.request('/api/rules', {auth: false});
    } catch {
      rules = null;
    } finally {
      loading = false;
      paint();
    }
  };
  i18n.onChange(paint);
})();
