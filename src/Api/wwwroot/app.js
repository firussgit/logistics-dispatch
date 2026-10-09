(() => {
  const jobs = new Map();
  const drivers = new Map();
  const $ = (id) => document.getElementById(id);
  const STATUSES = ['Pending', 'Assigned', 'InTransit', 'Completed', 'Cancelled'];
  const GROUP = 'dispatchers';
  const CENTER = { lat: 40.7128, lng: -74.006 };

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

  function jobRow(j) {
    const tr = el('tr');
    tr.dataset.id = j.id;
    tr.append(el('td', { textContent: j.reference }), el('td', { textContent: j.customerName }));
    tr.append(el('td', {}, el('span', { className: `badge status-${j.status.toLowerCase()}`, textContent: j.status })));

    const driverCell = el('td');
    if (j.status === 'Pending') {
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
      el('div', {}, el('span', { className: `dot ${d.status.toLowerCase()}` }), d.name,
        el('small', { textContent: `${d.currentLocation.lat.toFixed(4)}, ${d.currentLocation.lng.toFixed(4)}` })),
      el('span', { textContent: d.status }))));
    renderTiles();
  }

  // ---------- data ----------
  async function resync() {
    const [js, ds] = await Promise.all([api('/api/jobs'), api('/api/drivers')]);
    jobs.clear(); js.forEach((j) => jobs.set(j.id, j));
    drivers.clear(); ds.forEach((d) => drivers.set(d.id, d));
    renderDrivers(); renderJobs();
  }

  async function refreshJob(id) {
    jobs.set(id, await api(`/api/jobs/${id}`));
    renderJobs();
  }

  // ---------- SignalR ----------
  const setConn = (cls, text) => { const c = $('conn'); c.className = `conn ${cls}`; c.textContent = text; };
  const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/dispatch').withAutomaticReconnect().build();

  conn.on('JobCreated', (j) => { jobs.set(j.id, j); renderJobs(); });
  conn.on('JobStatusChanged', (e) => refreshJob(e.jobId).catch(() => {}));
  conn.on('DriverUpdated', (d) => { drivers.set(d.id, d); renderDrivers(); renderJobs(); });
  conn.on('JobProgress', (e) => {
    const j = jobs.get(e.jobId);
    if (!j) return;
    Object.assign(j, { etaSeconds: e.etaSeconds, progress: e.progress, currentLocation: { lat: e.lat, lng: e.lng } });
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
