(async () => {
  await Auth.require(['Dispatcher']);
  const $ = (id) => document.getElementById(id);
  const el = (tag, props = {}, ...kids) => {
    const n = Object.assign(document.createElement(tag), props);
    kids.forEach((k) => n.append(k));
    return n;
  };
  const mins = (v) => (v == null ? '—' : v < 1 ? `${Math.round(v * 60)}s` : `${v.toFixed(1)} min`);
  const pct = (v) => (v == null ? '—' : `${Math.round(v * 100)}%`);

  function toast(message, kind = 'error') {
    const t = el('div', { className: `toast ${kind}`, textContent: message });
    $('toasts').append(t);
    setTimeout(() => t.remove(), 4000);
  }

  async function api(path, options) {
    const res = await fetch(path, options);
    if (res.status === 401) Auth.expired();
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    return res.json();
  }

  function render(s) {
    const tiles = [
      ['Orders', s.orders], ['Delivered', s.completed], ['Cancelled', s.cancelled], ['In progress', s.active],
      ['Completion rate', pct(s.completionRate)], ['Avg delivery', mins(s.avgDeliveryMinutes)],
      ['Median delivery', mins(s.medianDeliveryMinutes)], ['Avg wait for driver', mins(s.avgWaitForDriverMinutes)],
      ['Orders / hour', s.ordersPerHour.toFixed(1)],
    ];
    $('tiles').replaceChildren(...tiles.map(([label, v]) => el('div', { className: 'tile' }, el('b', { textContent: v }), el('span', { textContent: label }))));

    const max = Math.max(1, ...s.perHour.map((h) => h.orders));
    $('chart').replaceChildren(...s.perHour.map((h) => {
      const col = el('div', { className: `col${h.orders ? '' : ' zero'}`, title: `${new Date(h.hourStart).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' })}: ${h.orders} order(s)` });
      col.style.height = `${(h.orders / max) * 100}%`;
      return col;
    }));
    $('axis-from').textContent = new Date(s.perHour[0].hourStart).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' });
    $('axis-to').textContent = 'now';

    $('drivers').replaceChildren(...s.drivers.map((d) => el('tr', {},
      el('td', { textContent: d.isAutomated ? d.name : `${d.name} (human)` }),
      el('td', { textContent: d.completed }),
      el('td', { textContent: mins(d.avgDeliveryMinutes) }),
      el('td', { textContent: d.offersReceived }),
      el('td', { textContent: d.offersAccepted }),
      el('td', { textContent: pct(d.acceptanceRate) }))));
  }

  async function refresh() {
    try { render(await api(`/api/stats?hours=${$('hours').value}`)); } catch (e) { toast(e.message); }
  }

  $('hours').onchange = refresh;
  $('rush-btn').onclick = async () => {
    const btn = $('rush-btn');
    btn.disabled = true;
    try {
      const r = await api(`/api/stats/rush?count=${$('rush-count').value}`, { method: 'POST' });
      toast(`${r.created} rush orders placed`, 'info');
      await refresh();
    } catch (e) { toast(e.message); }
    finally { btn.disabled = false; }
  };

  await refresh();
  setInterval(refresh, 5000);
})();
