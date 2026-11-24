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
    if (row.type === 'armored') return t('guide.armored', {structure: row.fenceDamage, keep: row.keepDamage});
    if (row.type === 'ranged') return t('guide.ranged', {structure: row.damage, keep: row.keepDamage});
    if (['apc', 'mine', 'frost', 'pulse'].includes(row.type)) return t(`guide.${row.type}`, {damage: row.damage, targets: row.maxTargets});
    if (row.keepDamage != null) return t('guide.unitDamage', {keep: row.keepDamage, fence: row.fenceDamage});
    return row.damage;
  }

  function stat(key, value, symbol) {
    const group = document.createElement('div');
    const label = document.createElement('dt');
    label.append(icon(symbol, 'guide-stat-icon'), element('span', t(key)));
    group.append(label, element('dd', value));
    return group;
  }

  function cards(title, rows) {
    const section = document.createElement('section');
    section.className = 'guide-section';
    const heading = element('h3', t(title));
    heading.id = `rules-${title}`;
    const list = document.createElement('ul');
    list.className = 'guide-cards';
    list.setAttribute('role', 'list');
    list.setAttribute('aria-labelledby', heading.id);
    for (const row of rows) {
      const card = document.createElement('li');
      card.className = 'guide-card';
      const portrait = document.createElement('span');
      portrait.className = 'ddlc-snap guide-portrait';
      portrait.append(icon(row.type, 'guide-sprite'));
      const name = document.createElement('div');
      name.className = 'guide-card-name';
      name.append(portrait, element('h4', t(`action.${row.type}`)));

      const hp = row.hpFirst === row.hpLast ? row.hpFirst : `${row.hpFirst}–${row.hpLast}`;
      const stats = document.createElement('dl');
      stats.className = 'guide-card-stats';
      stats.append(
        stat('guideHp', hp, 'heart'),
        stat('guideRadius', row.radius, 'attack-radius'),
        stat('guideInterval', row.interval, 'attack-interval'),
      );
      if (row.stepTicks != null)
        stats.append(stat('guideSpeed', row.stepTicks.toLocaleString(document.documentElement.lang), 'runner'));
      stats.append(stat('guidePrice', row.price ?? '—', 'coin'));
      if (row.count != null) stats.append(stat('guideCount', row.count, 'unit-group'));
      const effect = stat('guideDamage', damage(row), 'hit-spark');
      effect.className = 'guide-card-effect';
      stats.append(effect);
      card.append(name, stats);
      list.append(card);
    }
    section.append(heading, list);
    return section;
  }

  function budgetTable(budgets) {
    const node = document.createElement('table');
    node.className = 'guide-table';
    node.append(element('caption', t('guideBudgets')));
    const head = node.createTHead().insertRow();
    for (const key of ['guideWave', 'defender', 'attacker', 'guideStructureBonus'])
      head.append(Object.assign(element('th', t(key)), {scope: 'col'}));

    const section = node.createTBody();
    for (const {wave, defender, attacker, bonusPerStructure} of budgets) {
      const line = section.insertRow();
      line.append(Object.assign(element('th', wave), {scope: 'row'}));
      for (const value of [defender, attacker, bonusPerStructure ? `+${bonusPerStructure}` : '—'])
        line.insertCell().textContent = value;
    }
    return node;
  }

  function paint() {
    status.hidden = Boolean(rules);
    status.textContent = rules ? '' : t(loading ? 'loading' : 'guideUnavailable');
    if (!rules) return;
    const notes = element('p', `${t('guideNotes', {total: rules.waveCount})}. ${t('guideSpeedNote')}`);
    notes.className = 'fine';
    const economy = element('p', t('guideEconomy'));
    economy.className = 'fine';
    body.replaceChildren(cards('guideUnits', rules.units), cards('guideStructures', rules.structures), notes,
      budgetTable(rules.budgets), economy);
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
