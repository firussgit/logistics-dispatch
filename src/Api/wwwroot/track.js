(() => {
  const $ = (id) => document.getElementById(id);
  const id = new URLSearchParams(location.search).get('id');
  const GROUP = 'dispatchers';
  const map = DispatchMap('map');
  let job = null;
  let driver = null;

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

  async function getJson(url) {
    const r = await fetch(url);
    if (!r.ok) throw new Error(r.status === 404 ? 'We could not find that delivery.' : `Error ${r.status}`);
    return r.json();
  }

  function renderSummary() {
    const s = $('summary');
    s.replaceChildren();
    const left = el('div', {}, el('small', { textContent: job.reference }), el('h2', { textContent: `Delivery for ${job.customerName}` }),
      el('small', { textContent: driver ? `Driver: ${driver.name}` : 'Driver: not assigned yet' }));
    const etaText = job.status === 'Completed' ? 'Delivered' : job.status === 'Cancelled' ? 'Cancelled' : (job.status === 'InTransit' || job.status === 'Assigned') ? fmtEta(job.etaSeconds) : '—';
    const etaLabel = job.status === 'InTransit' ? 'estimated arrival' : job.status === 'Assigned' ? 'driver reaches pickup in' : '';
    const right = el('div', { className: 'eta' }, document.createTextNode(etaText), el('small', { textContent: etaLabel }));
    s.append(el('div', { className: 'hero' }, left, right));
    if (job.status === 'InTransit') {
      const bar = el('div', { className: 'bar', style: 'width:100%;margin-top:12px' }, el('i'));
      bar.firstChild.style.width = `${Math.round(job.progress * 100)}%`;
      s.append(bar);
    }
    if (job.status === 'Cancelled') s.append(el('div', { className: 'cancelled-banner', textContent: 'This delivery was cancelled.' }));
  }

  function renderTimeline() {
    const at = Object.fromEntries((job.history || []).map((h) => [h.to, h.at]));
    const idx = STEPS.findIndex(([s]) => s === job.status);
    $('timeline').replaceChildren(...STEPS.map(([status, title, sub], i) => {
      const li = el('li', { className: job.status === 'Cancelled' ? (at[status] ? 'done' : '') : i < idx || job.status === 'Completed' ? 'done' : i === idx ? 'current' : '' });
      li.append(el('span', { className: 'dot2' }), el('div', {}, document.createTextNode(title), el('small', { textContent: at[status] ? `${fmtTime(at[status])} · ${sub}` : sub })));
      return li;
    }));
  }

  async function load() {
    job = await getJson(`/api/jobs/${id}`);
    driver = job.driverId ? (await getJson('/api/drivers')).find((d) => d.id === job.driverId) ?? null : driver;
    if (driver) {
      // While assigned or in transit the job's live position is the driver's position.
      map.setDriver({ ...driver, currentLocation: job.status === 'InTransit' || job.status === 'Assigned' ? job.currentLocation : driver.currentLocation });
    }
    map.setJob(job);
    map.focusJob(job, driver?.id);
    renderSummary();
    renderTimeline();
  }

  const setConn = (cls, text) => { const c = $('conn'); c.className = `conn ${cls}`; c.textContent = text; };
  const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/dispatch').withAutomaticReconnect().build();

  conn.on('JobStatusChanged', (e) => { if (e.jobId === id) load().catch(() => {}); });
  conn.on('RouteReady', (e) => { if (e.jobId === id) map.loadRoute(id, true); });
  conn.on('JobProgress', (e) => {
    if (e.jobId !== id || !job) return;
    Object.assign(job, { etaSeconds: e.etaSeconds, progress: e.progress, currentLocation: { lat: e.lat, lng: e.lng } });
    if (job.driverId) map.moveDriver(job.driverId, e.lat, e.lng);
    renderSummary();
  });
  conn.onreconnecting(() => setConn('reconnecting', 'Reconnecting…'));
  conn.onclose(() => setConn('offline', 'Offline'));
  conn.onreconnected(async () => { setConn('online', 'Live'); await conn.invoke('JoinDispatchGroup', GROUP); await load(); });

  (async () => {
    if (!id) { $('summary').textContent = 'No delivery specified. Use the tracking link you were given.'; return; }
    try { await load(); } catch (e) { $('summary').textContent = e.message; return; }
    try { await conn.start(); await conn.invoke('JoinDispatchGroup', GROUP); setConn('online', 'Live'); }
    catch { setConn('offline', 'Offline'); }
  })();
})();
