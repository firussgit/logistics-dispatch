// Public delivery tracking. No account: the link's secret token is the only credential, and it unlocks exactly one delivery.
(() => {
  const $ = (id) => document.getElementById(id);
  const token = new URLSearchParams(location.search).get('t');
  const map = DispatchMap('map', { routeUrl: () => `/api/track/${encodeURIComponent(token)}/route` });
  let t = null; // TrackingDto

  const el = (tag, props = {}, ...kids) => {
    const n = Object.assign(document.createElement(tag), props);
    kids.forEach((k) => n.append(k));
    return n;
  };

  const STEPS = [
    ['Pending', 'Order received', 'Waiting for a driver'],
    ['Assigned', 'Driver assigned', 'Your driver is heading to the pickup'],
    ['InTransit', 'On the way', 'Your delivery is moving'],
    ['Completed', 'Delivered', 'Enjoy!'],
  ];
  const fmtEta = (s) => (s == null ? '—' : s >= 60 ? `${Math.floor(s / 60)} min ${s % 60}s` : `${s}s`);
  const fmtTime = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  async function getTracking() {
    const r = await fetch(`/api/track/${encodeURIComponent(token)}`);
    if (!r.ok) throw new Error(r.status === 404 ? 'We could not find that delivery. Check the link you were given.' : `Something went wrong (${r.status}).`);
    return r.json();
  }

  function renderSummary() {
    const s = $('summary');
    s.replaceChildren();
    const left = el('div', {}, el('small', { textContent: t.reference }), el('h2', { textContent: 'Your delivery' }),
      el('small', { textContent: t.driverName ? `Driver: ${t.driverName}` : 'Driver: not assigned yet' }));
    const moving = t.status === 'InTransit' || t.status === 'Assigned';
    const etaText = t.status === 'Completed' ? 'Delivered' : t.status === 'Cancelled' ? 'Cancelled' : moving ? fmtEta(t.etaSeconds) : '—';
    const etaLabel = t.status === 'InTransit' ? 'estimated arrival' : t.status === 'Assigned' ? 'driver reaches pickup in' : '';
    s.append(el('div', { className: 'hero' }, left, el('div', { className: 'eta' }, document.createTextNode(etaText), el('small', { textContent: etaLabel }))));
    if (t.status === 'InTransit') {
      const bar = el('div', { className: 'bar', style: 'width:100%;margin-top:12px' }, el('i'));
      bar.firstChild.style.width = `${Math.round(t.progress * 100)}%`;
      s.append(bar);
    }
    if (t.status === 'Cancelled') s.append(el('div', { className: 'cancelled-banner', textContent: 'This delivery was cancelled.' }));
  }

  function renderTimeline() {
    const at = Object.fromEntries((t.history || []).map((h) => [h.to, h.at]));
    const idx = STEPS.findIndex(([s]) => s === t.status);
    $('timeline').replaceChildren(...STEPS.map(([status, title, sub], i) => {
      const cls = t.status === 'Cancelled' ? (at[status] ? 'done' : '') : i < idx || t.status === 'Completed' ? 'done' : i === idx ? 'current' : '';
      const li = el('li', { className: cls });
      li.append(el('span', { className: 'dot2' }), el('div', {}, document.createTextNode(title), el('small', { textContent: at[status] ? `${fmtTime(at[status])} · ${sub}` : sub })));
      return li;
    }));
  }

  /** The map's job shape (it doesn't need, and we don't have, the customer's details). */
  const mapJob = () => ({ id: t.id, status: t.status, reference: t.reference, customerName: '', pickup: t.pickup, dropoff: t.dropoff });

  async function load(first = false) {
    t = await getTracking();
    if (t.driverName && t.driverLocation) {
      // While assigned or in transit the job's live position is the driver's position.
      const here = t.status === 'InTransit' || t.status === 'Assigned' ? t.currentLocation : t.driverLocation;
      map.setDriver({ id: 'driver', name: t.driverName, status: 'Busy', currentLocation: here });
    }
    map.setJob(mapJob());
    if (first) map.focusJob(mapJob(), 'driver');
    renderSummary();
    renderTimeline();
  }

  const setConn = (cls, text) => { const c = $('conn'); c.className = `conn ${cls}`; c.textContent = text; };
  const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/track').withAutomaticReconnect().build();

  conn.on('JobStatusChanged', (e) => { if (t && e.jobId === t.id) load().catch(() => {}); });
  conn.on('RouteReady', (e) => { if (t && e.jobId === t.id) map.loadRoute(t.id, true); });
  conn.on('JobProgress', (e) => {
    if (!t || e.jobId !== t.id) return;
    Object.assign(t, { etaSeconds: e.etaSeconds, progress: e.progress, currentLocation: { lat: e.lat, lng: e.lng } });
    map.moveDriver('driver', e.lat, e.lng);
    renderSummary();
  });

  // The server forgets which delivery a connection follows when it drops, so ask again after every (re)connect.
  async function follow() {
    if (!(await conn.invoke('Track', token))) throw new Error('This tracking link is not valid.');
    setConn('online', 'Live');
  }

  conn.onreconnecting(() => setConn('reconnecting', 'Reconnecting…'));
  conn.onclose(() => setConn('offline', 'Offline'));
  conn.onreconnected(async () => { await follow(); await load(); });

  (async () => {
    if (!token) { $('summary').textContent = 'No delivery specified. Use the tracking link you were given.'; return; }
    try { await load(true); } catch (e) { $('summary').textContent = e.message; setConn('offline', 'Not found'); return; }
    try { await conn.start(); await follow(); } catch { setConn('offline', 'Offline'); }
  })();
})();
