(() => {
  const jobs = new Map();
  const drivers = new Map();
  const $ = (id) => document.getElementById(id);
  const STATUSES = ['Pending', 'Assigned', 'InTransit', 'Completed', 'Cancelled'];
  const GROUP = 'dispatchers';
  const CENTER = { lat: 40.7128, lng: -74.006 };
  const map = DispatchMap('map');

  const el = (tag, props = {}, ...kids) => {
    const n = Object.assign(document.createElement(tag), props);
    kids.forEach((k) => n.append(k));
    return n;
  };

  function toast(message, kind = 'error') {
    const t = el('div', { className: `toast ${kind}`, textContent: message });
    $('toasts').append(t);
    setTimeout(() => t.remove(), 4500);
  }

  async function api(path, options) {
    const res = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
    if (!res.ok) {
      let msg = `${res.status} ${res.statusText}`;
      try {
        const p = await res.json();
        msg = p.detail || (p.errors && Object.values(p.errors).flat().join(' ')) || p.title || msg;
      } catch { /* non-JSON error body */ }
      throw new Error(msg);
    }
    return res.status === 204 ? null : res.json();
  }
  const act = (p) => p.catch((e) => toast(e.message));

  // ---------- rendering ----------
  function renderTiles() {
    const counts = Object.fromEntries(STATUSES.map((s) => [s, 0]));
    jobs.forEach((j) => counts[j.status]++);
    const idle = [...drivers.values()].filter((d) => d.status === 'Idle').length;
    const tiles = [...STATUSES.map((s) => [s, counts[s]]), ['Idle drivers', idle]];
    $('tiles').replaceChildren(...tiles.map(([label, n]) => el('div', { className: 'tile' }, el('b', { textContent: n }), el('span', { textContent: label }))));
  }

  function driverName(id) { return id ? (drivers.get(id)?.name ?? id.slice(0, 8)) : '—'; }
  const fmtEta = (s) => (s == null ? '—' : s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`);

  // ---------- offers ----------
  const offers = new Map(); // open (Pending) offers by id

  const secsLeft = (o) => Math.max(0, Math.ceil((Date.parse(o.expiresAt) - Date.now()) / 1000));
  const openOfferFor = (jobId) => [...offers.values()].find((o) => o.jobId === jobId && Date.parse(o.expiresAt) > Date.now());
  const offerNoteText = (o) => `Offered to ${driverName(o.driverId)} · ${secsLeft(o)}s`;

  // live countdown on pending rows without re-rendering the table
  setInterval(() => {
    document.querySelectorAll('.offer-note').forEach((n) => {
      const left = Math.max(0, Math.ceil((Date.parse(n.dataset.expires) - Date.now()) / 1000));
      n.textContent = `Offered to ${n.dataset.driver} · ${left}s`;
    });
  }, 500);

  const activity = [];
  function logActivity(text, kind = '') {
    activity.unshift({ at: new Date(), text, kind });
    activity.length = Math.min(activity.length, 14);
    $('activity').replaceChildren(...activity.map((a) => el('li', { className: a.kind },
      el('time', { textContent: a.at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) }),
      document.createTextNode(a.text))));
  }

  function onOffer(o, created) {
    const who = driverName(o.driverId);
    if (o.status === 'Pending') {
      offers.set(o.id, o);
      if (created) logActivity(`${o.reference} offered to ${who} (${money(o.payout)})`, 'offer');
    } else {
      const had = offers.delete(o.id);
      if (had || !created) {
        const verb = { Accepted: 'accepted', Declined: 'declined', Expired: 'did not answer', Cancelled: 'offer withdrawn for' }[o.status];
        logActivity(o.status === 'Cancelled' ? `${o.reference}: offer to ${who} withdrawn` : `${who} ${verb} ${o.reference}`, o.status.toLowerCase());
      }
    }
    renderJobs();
  }

  const money = (v) => `$${Number(v).toFixed(2)}`;

  function jobRow(j) {
    const tr = el('tr', { className: 'clickable' });
    tr.onclick = (ev) => { if (!ev.target.closest('select,button,a')) map.focusJob(j, j.driverId); };
    tr.dataset.id = j.id;
    tr.append(el('td', {}, el('a', { className: 'link', href: `track.html?id=${j.id}`, target: '_blank', textContent: j.reference })), el('td', { textContent: j.customerName }));
    tr.append(el('td', {}, el('span', { className: `badge status-${j.status.toLowerCase()}`, textContent: j.status })));

    const driverCell = el('td');
    if (j.status === 'Pending') {
      const open = openOfferFor(j.id);
      if (open) {
        const note = el('div', { className: 'offer-note', textContent: offerNoteText(open) });
        note.dataset.expires = open.expiresAt;
        note.dataset.driver = driverName(open.driverId);
        driverCell.append(note);
      }
      const sel = el('select');
      sel.append(el('option', { value: '', textContent: 'Assign…' }));
      [...drivers.values()].filter((d) => d.status === 'Idle').forEach((d) => sel.append(el('option', { value: d.id, textContent: d.name })));
      sel.onchange = () => sel.value && act(api(`/api/jobs/${j.id}/assign`, { method: 'POST', body: JSON.stringify({ driverId: sel.value }) }));
      driverCell.append(sel);
    } else driverCell.textContent = driverName(j.driverId);
    tr.append(driverCell);

    const bar = el('div', { className: 'bar' }, el('i'));
    bar.firstChild.style.width = `${Math.round((j.progress || 0) * 100)}%`;
    tr.append(el('td', { className: 'progress' }, bar), el('td', { className: 'eta', textContent: fmtEta(j.etaSeconds) }));

    const actions = el('td');
    if (j.status === 'Pending' || j.status === 'Assigned') {
      actions.append(el('button', { textContent: 'Cancel', onclick: () => act(api(`/api/jobs/${j.id}/cancel`, { method: 'POST' })) }));
    }
    tr.append(actions);
    return tr;
  }

  function renderJobs() {
    const sorted = [...jobs.values()].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    $('jobs').replaceChildren(...sorted.map(jobRow));
    renderTiles();
  }

  function renderDrivers() {
    $('drivers').replaceChildren(...[...drivers.values()].map((d) => el('li', {},
      el('div', {}, el('span', { className: `dot ${d.status.toLowerCase()}` }),
        d.isAutomated ? document.createTextNode(d.name) : el('a', { className: 'link', href: `driver.html?id=${d.id}`, target: '_blank', textContent: `${d.name} ↗` }),
        el('small', { textContent: d.isAutomated ? 'simulated driver' : 'human driver · open driver app' })),
      el('span', { textContent: d.status }))));
    renderTiles();
  }

  // ---------- data ----------
  async function resync() {
    const [js, ds, os] = await Promise.all([api('/api/jobs'), api('/api/drivers'), api('/api/offers?status=pending')]);
    jobs.clear(); js.forEach((j) => jobs.set(j.id, j));
    drivers.clear(); ds.forEach((d) => drivers.set(d.id, d));
    offers.clear(); os.forEach((o) => offers.set(o.id, o));
    renderDrivers(); renderJobs();
    drivers.forEach((d) => map.setDriver(d));
    jobs.forEach((j) => map.setJob(j));
    if (!resync.fitted) { map.fitAll(); resync.fitted = true; }
  }

  async function refreshJob(id) {
    const j = await api(`/api/jobs/${id}`);
    jobs.set(id, j);
    map.setJob(j);
    renderJobs();
  }

  // ---------- SignalR ----------
  const setConn = (cls, text) => { const c = $('conn'); c.className = `conn ${cls}`; c.textContent = text; };
  const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/dispatch').withAutomaticReconnect().build();

  conn.on('OfferCreated', (o) => onOffer(o, true));
  conn.on('OfferUpdated', (o) => onOffer(o, false));
  conn.on('JobCreated', (j) => { jobs.set(j.id, j); map.setJob(j); renderJobs(); logActivity(`${j.reference} created for ${j.customerName}`); });
  conn.on('JobStatusChanged', (e) => refreshJob(e.jobId).catch(() => {}));
  conn.on('DriverUpdated', (d) => { drivers.set(d.id, d); map.setDriver(d); renderDrivers(); renderJobs(); });
  conn.on('JobProgress', (e) => {
    const j = jobs.get(e.jobId);
    if (!j) return;
    Object.assign(j, { etaSeconds: e.etaSeconds, progress: e.progress, currentLocation: { lat: e.lat, lng: e.lng } });
    if (j.driverId) map.moveDriver(j.driverId, e.lat, e.lng);
    const row = document.querySelector(`tr[data-id="${e.jobId}"]`);
    if (!row) return;
    row.querySelector('.progress i').style.width = `${Math.round(e.progress * 100)}%`;
    row.querySelector('.eta').textContent = fmtEta(e.etaSeconds);
  });

  conn.onreconnecting(() => setConn('reconnecting', 'Reconnecting…'));
  conn.onclose(() => setConn('offline', 'Offline'));
  conn.onreconnected(async () => {
    setConn('online', 'Connected');
    await conn.invoke('JoinDispatchGroup', GROUP);
    await resync();
    toast('Reconnected — data refreshed', 'info');
  });

  async function start() {
    try {
      await conn.start();
      await conn.invoke('JoinDispatchGroup', GROUP);
      setConn('online', 'Connected');
      await resync();
    } catch (e) {
      setConn('offline', 'Offline');
      if (conn.state === signalR.HubConnectionState.Disconnected) setTimeout(start, 3000);
    }
  }

  // ---------- form ----------
  const jitter = (v) => +(v + (Math.random() - 0.5) * 0.06).toFixed(5);
  $('random-btn').onclick = () => {
    const f = $('job-form');
    f.pLat.value = jitter(CENTER.lat); f.pLng.value = jitter(CENTER.lng);
    f.dLat.value = jitter(CENTER.lat); f.dLng.value = jitter(CENTER.lng);
    if (!f.customerName.value) f.customerName.value = `Customer ${Math.floor(Math.random() * 900 + 100)}`;
  };
  $('job-form').onsubmit = async (ev) => {
    ev.preventDefault();
    const f = ev.target;
    const body = {
      customerName: f.customerName.value,
      pickup: { lat: +f.pLat.value, lng: +f.pLng.value },
      dropoff: { lat: +f.dLat.value, lng: +f.dLng.value },
    };
    await act(api('/api/jobs', { method: 'POST', body: JSON.stringify(body) }));
    f.reset();
  };

  start();
})();
