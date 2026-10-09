(() => {
  const $ = (id) => document.getElementById(id);
  const GROUP = 'dispatchers';
  const map = DispatchMap('map');

  let me = null;          // DriverDto
  let offer = null;       // the open (or just-closed) offer shown on screen
  let activeJob = null;   // JobDto this driver is currently working on
  let stats = { accepted: 0, earnings: 0 };

  const el = (tag, props = {}, ...kids) => {
    const n = Object.assign(document.createElement(tag), props);
    kids.forEach((k) => n.append(k));
    return n;
  };
  const km = (m) => `${(m / 1000).toFixed(1)} km`;
  const money = (v) => `$${Number(v).toFixed(2)}`;
  const fmtEta = (s) => (s == null ? '—' : s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`);

  function toast(message, kind = 'error') {
    const t = el('div', { className: `toast ${kind}`, textContent: message });
    $('toasts').append(t);
    setTimeout(() => t.remove(), 4000);
  }

  async function api(path, options) {
    const res = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
    if (!res.ok) {
      let msg = `${res.status} ${res.statusText}`;
      try { const p = await res.json(); msg = p.detail || p.title || msg; } catch { /* non-JSON */ }
      throw new Error(msg);
    }
    return res.json();
  }

  // ------------------------------------------------------------ rendering
  function renderMe() {
    const s = me.status.toLowerCase();
    const label = me.status === 'Idle' ? 'Online' : me.status === 'Busy' ? 'On a job' : 'Offline';
    $('me').replaceChildren(
      el('div', {}, el('h2', { textContent: me.name }), el('small', { textContent: 'Driver app' })),
      el('span', { className: `pill ${s}`, textContent: label }));
  }

  function renderStats() {
    $('stats').replaceChildren(...[['Accepted', stats.accepted], ['Earnings', money(stats.earnings)]].map(([label, v]) =>
      el('div', {}, el('b', { textContent: v }), el('span', { textContent: label }))));
  }

  function renderOffer() {
    const card = $('offer');
    if (!offer) { card.classList.add('hidden'); return; }
    const open = offer.status === 'Pending';
    card.classList.remove('hidden');
    card.classList.toggle('closed', !open);

    const total = Date.parse(offer.expiresAt) - Date.parse(offer.createdAt);
    const timer = el('div', { className: 'timer', id: 'timer' }, el('i'));
    const timerText = el('div', { className: 'timer-text', id: 'timer-text' });

    const actions = el('div', { className: 'actions' },
      el('button', { className: 'decline', textContent: 'Decline', disabled: !open, onclick: () => answer('decline') }),
      el('button', { className: 'accept', textContent: 'Accept', disabled: !open, onclick: () => answer('accept') }));

    card.replaceChildren(
      el('div', { className: 'job-line' }, el('b', { textContent: open ? 'New delivery offer' : `Offer ${offer.status.toLowerCase()}` }), el('small', { textContent: offer.reference })),
      el('div', { className: 'payout', textContent: money(offer.payout) }),
      el('div', { className: 'meta' },
        el('div', {}, el('b', { textContent: km(offer.distanceToPickupMeters) }), document.createTextNode('to pickup')),
        el('div', {}, el('b', { textContent: km(offer.tripMeters) }), document.createTextNode('trip')),
        el('div', {}, el('b', { textContent: offer.customerName }), document.createTextNode('customer'))),
      timer, timerText, actions);
    card.dataset.total = total;
    tickTimer();

    map.setJob({ id: offer.jobId, status: 'Pending', reference: offer.reference, customerName: offer.customerName, pickup: offer.pickup, dropoff: offer.dropoff });
    if (open) map.focusJob({ pickup: offer.pickup, dropoff: offer.dropoff }, me.id);
  }

  function tickTimer() {
    if (!offer || offer.status !== 'Pending') return;
    const t = $('timer');
    if (!t) return;
    const left = (Date.parse(offer.expiresAt) - Date.now()) / 1000;
    const total = Number($('offer').dataset.total) / 1000;
    t.firstChild.style.width = `${Math.max(0, Math.min(100, (left / total) * 100))}%`;
    t.classList.toggle('low', left < 5);
    $('timer-text').textContent = left > 0 ? `${Math.ceil(left)}s left to respond` : 'Time is up…';
  }
  setInterval(tickTimer, 250);

  function renderJob() {
    const card = $('job');
    if (!activeJob) { card.classList.add('hidden'); return; }
    card.classList.remove('hidden');
    const j = activeJob;
    const headline = j.status === 'Assigned' ? 'Head to the pickup' : 'Deliver the order';
    const bar = el('div', { className: 'bar', style: 'width:100%;margin-top:10px' }, el('i'));
    bar.firstChild.style.width = `${Math.round((j.progress || 0) * 100)}%`;
    card.replaceChildren(
      el('div', { className: 'job-line' }, el('b', { textContent: headline }), el('span', { className: `badge status-${j.status.toLowerCase()}`, textContent: j.status })),
      el('p', { textContent: `${j.reference} · ${j.customerName}`, style: 'margin:8px 0 0' }),
      el('p', { textContent: `${j.status === 'Assigned' ? 'Pickup in' : 'Dropoff in'} ${fmtEta(j.etaSeconds)}`, style: 'margin:4px 0 0;color:var(--muted)' }),
      bar);
  }

  function renderIdle() {
    $('idle').classList.toggle('hidden', !!(offer && offer.status === 'Pending') || !!activeJob || me.status !== 'Idle');
  }

  function renderAll() { renderMe(); renderOffer(); renderJob(); renderIdle(); renderStats(); }

  // ------------------------------------------------------------ actions
  async function answer(kind) {
    const o = offer;
    try {
      await api(`/api/offers/${o.id}/${kind}`, { method: 'POST' });
      if (kind === 'accept') toast(`Accepted ${o.reference}`, 'info');
    } catch (e) {
      toast(e.message);
    }
    await refreshAll(); // pick up the authoritative state either way
  }

  // ------------------------------------------------------------ data
  async function loadMe() {
    const drivers = await api('/api/drivers');
    const wanted = new URLSearchParams(location.search).get('id');
    me = drivers.find((d) => d.id === wanted) ?? drivers.find((d) => !d.isAutomated) ?? drivers[0];
    map.setDriver(me);
  }

  async function refreshAll() {
    await loadMe();
    const [open, accepted, jobs] = await Promise.all([
      api(`/api/offers?status=pending&driverId=${me.id}`),
      api(`/api/offers?status=accepted&driverId=${me.id}`),
      api('/api/jobs'),
    ]);
    stats = { accepted: accepted.length, earnings: accepted.reduce((s, o) => s + o.payout, 0) };
    const live = open.find((o) => Date.parse(o.expiresAt) > Date.now());
    if (live) offer = live;
    else if (offer && offer.status === 'Pending') offer = null; // vanished or timed out while we were away

    activeJob = jobs.find((j) => j.driverId === me.id && (j.status === 'Assigned' || j.status === 'InTransit')) ?? null;
    if (activeJob) {
      map.setJob(activeJob);
    } else if (!offer) {
      for (const j of jobs) map.removeJob(j.id);
    }
    renderAll();
  }

  // ------------------------------------------------------------ SignalR
  const setConn = (cls, text) => { const c = $('conn'); c.className = `conn ${cls}`; c.textContent = text; };
  const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/dispatch').withAutomaticReconnect().build();

  conn.on('RouteReady', (e) => map.loadRoute(e.jobId, true));

  conn.on('OfferCreated', (o) => {
    if (!me || o.driverId !== me.id) return;
    offer = o;
    renderAll();
    toast(`New offer: ${money(o.payout)}`, 'info');
  });

  conn.on('OfferUpdated', (o) => {
    if (!me || o.driverId !== me.id) return;
    if (offer && offer.id === o.id) {
      offer = o;
      if (o.status === 'Expired') toast('Offer expired — it went to the next driver');
      if (o.status === 'Cancelled') toast('Offer withdrawn by dispatch', 'info');
      if (o.status !== 'Pending') setTimeout(() => { if (offer?.id === o.id) { offer = null; refreshAll().catch(() => {}); } }, 4000);
    }
    refreshAll().catch(() => {});
  });

  conn.on('JobStatusChanged', (e) => { if (me && (e.driverId === me.id || activeJob?.id === e.jobId)) refreshAll().catch(() => {}); });
  conn.on('DriverUpdated', (d) => { if (me && d.id === me.id) { me = d; map.setDriver(d); renderAll(); } });
  conn.on('JobProgress', (e) => {
    if (!activeJob || e.jobId !== activeJob.id) return;
    Object.assign(activeJob, { etaSeconds: e.etaSeconds, progress: e.progress });
    map.moveDriver(me.id, e.lat, e.lng);
    renderJob();
  });

  conn.onreconnecting(() => setConn('reconnecting', 'Reconnecting…'));
  conn.onclose(() => setConn('offline', 'Offline'));
  conn.onreconnected(async () => { setConn('online', 'Online'); await conn.invoke('JoinDispatchGroup', GROUP); await refreshAll(); });

  (async () => {
    try { await refreshAll(); map.fitAll(); } catch (e) { $('me').textContent = e.message; return; }
    try { await conn.start(); await conn.invoke('JoinDispatchGroup', GROUP); setConn('online', 'Online'); }
    catch { setConn('offline', 'Offline'); }
  })();
})();
