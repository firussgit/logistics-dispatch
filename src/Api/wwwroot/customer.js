(async () => {
  const me = await Auth.require(['Customer']);
  const $ = (id) => document.getElementById(id);
  const map = DispatchMap('map');
  const orders = new Map(); // jobId -> JobDto (this customer's orders only; the server enforces that)
  let pickup = null;
  let dropoff = null;

  const el = (tag, props = {}, ...kids) => {
    const n = Object.assign(document.createElement(tag), props);
    kids.forEach((k) => n.append(k));
    return n;
  };
  const fmtEta = (s) => (s == null ? '' : s >= 60 ? `${Math.floor(s / 60)} min ${s % 60}s` : `${s}s`);
  const fmtLoc = (l) => `${l.lat.toFixed(4)}, ${l.lng.toFixed(4)}`;
  const ACTIVE = new Set(['Pending', 'Assigned', 'InTransit']);

  function toast(message, kind = 'error') {
    const t = el('div', { className: `toast ${kind}`, textContent: message });
    $('toasts').append(t);
    setTimeout(() => t.remove(), 4500);
  }

  async function api(path, options) {
    const res = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
    if (res.status === 401) Auth.expired();
    if (!res.ok) {
      let msg = `${res.status} ${res.statusText}`;
      try {
        const p = await res.json();
        msg = (p.errors && Object.values(p.errors).flat().join(' ')) || p.detail || p.title || msg;
      } catch { /* non-JSON error */ }
      throw new Error(msg);
    }
    return res.json();
  }

  // ---------------------------------------------------------- order form
  function renderPicked() {
    for (const [id, loc] of [['pickup-box', pickup], ['dropoff-box', dropoff]]) {
      const box = $(id);
      box.classList.toggle('set', !!loc);
      box.lastChild.textContent = loc ? fmtLoc(loc) : 'not set';
    }
    $('place-btn').disabled = !(pickup && dropoff);
  }

  function setPoints(p, d) {
    pickup = p;
    dropoff = d;
    map.clearPoints();
    if (p) map.showPoint('pickup', p);
    if (d) map.showPoint('dropoff', d);
    renderPicked();
  }

  map.leaflet.on('click', (e) => {
    const loc = { lat: e.latlng.lat, lng: e.latlng.lng };
    if (!pickup) setPoints(loc, null);
    else if (!dropoff) setPoints(pickup, loc);
    else setPoints(loc, null); // third click starts over
  });

  const jitter = (v) => +(v + (Math.random() - 0.5) * 0.06).toFixed(5);
  $('random-btn').onclick = () => setPoints({ lat: jitter(40.7128), lng: jitter(-74.006) }, { lat: jitter(40.7128), lng: jitter(-74.006) });
  $('reset-btn').onclick = () => setPoints(null, null);

  $('order-form').onsubmit = async (ev) => {
    ev.preventDefault();
    $('place-btn').disabled = true;
    try {
      const job = await api('/api/jobs', { method: 'POST', body: JSON.stringify({ pickup, dropoff, notes: $('notes').value.trim() || null }) });
      orders.set(job.id, job);
      map.setJob(job);
      map.focusJob(job);
      setPoints(null, null);
      $('notes').value = '';
      render();
      toast(`Order ${job.reference} placed`, 'info');
    } catch (e) {
      toast(e.message);
      renderPicked();
    }
  };

  // ------------------------------------------------------------ orders
  function statusLine(j) {
    if (j.status === 'Pending') return 'Looking for a driver…';
    if (j.status === 'Assigned') return j.etaSeconds != null ? `Driver heading to the pickup · ${fmtEta(j.etaSeconds)}` : 'Driver assigned';
    if (j.status === 'InTransit') return j.etaSeconds != null ? `On the way · arriving in ${fmtEta(j.etaSeconds)}` : 'On the way';
    return j.status === 'Completed' ? 'Delivered' : 'Cancelled';
  }

  function orderCard(j) {
    const card = el('div', { className: `order${ACTIVE.has(j.status) ? ' active' : ''}` });
    card.dataset.id = j.id;
    const bar = el('div', { className: 'bar', style: 'width:100%' }, el('i'));
    bar.firstChild.style.width = `${Math.round((j.progress || 0) * 100)}%`;

    const actions = el('div', { className: 'actions' },
      el('a', { className: 'button', href: `track.html?t=${j.trackingToken}`, target: '_blank', textContent: 'Track' }),
      el('button', { textContent: 'Show on map', onclick: () => { map.setJob(j); map.focusJob(j, j.driverId); } }));
    if (j.status === 'Pending' || j.status === 'Assigned') {
      actions.append(el('button', {
        textContent: 'Cancel order',
        onclick: async () => { try { await api(`/api/jobs/${j.id}/cancel`, { method: 'POST' }); await refresh(j.id); } catch (e) { toast(e.message); } },
      }));
    }

    card.append(
      el('div', { className: 'top' }, el('b', { textContent: j.reference }), el('span', { className: `badge status-${j.status.toLowerCase()}`, textContent: j.status })),
      el('div', { className: 'eta', textContent: statusLine(j) }),
      ...(j.status === 'InTransit' ? [bar] : []),
      actions);
    return card;
  }

  function render() {
    const sorted = [...orders.values()].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    $('orders').replaceChildren(...(sorted.length ? sorted.map(orderCard) : [el('div', { className: 'empty', textContent: 'No orders yet.' })]));
  }

  async function refresh(id) {
    const j = await api(`/api/jobs/${id}`);
    orders.set(id, j);
    map.setJob(j);
    render();
  }

  // ----------------------------------------------------------- realtime
  const setConn = (cls, text) => { const c = $('conn'); c.className = `conn ${cls}`; c.textContent = text; };
  const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/dispatch').withAutomaticReconnect().build();

  conn.on('JobCreated', (j) => { if (!orders.has(j.id)) { orders.set(j.id, j); map.setJob(j); render(); } });
  conn.on('JobStatusChanged', (e) => refresh(e.jobId).catch(() => {}));
  conn.on('RouteReady', (e) => map.loadRoute(e.jobId, true));
  conn.on('JobProgress', (e) => {
    const j = orders.get(e.jobId);
    if (!j) return;
    Object.assign(j, { etaSeconds: e.etaSeconds, progress: e.progress, currentLocation: { lat: e.lat, lng: e.lng } });
    if (e.driverId) {
      // customers don't get to see the fleet, only the driver on their own order
      if (map.hasDriver(e.driverId)) map.moveDriver(e.driverId, e.lat, e.lng);
      else map.setDriver({ id: e.driverId, name: 'Your driver', status: 'Busy', currentLocation: { lat: e.lat, lng: e.lng } });
    }
    const card = document.querySelector(`.order[data-id="${e.jobId}"]`);
    if (card) card.replaceWith(orderCard(j)); // cheap enough: one small card
  });

  async function load() {
    const list = await api('/api/jobs');
    orders.clear();
    list.forEach((j) => { orders.set(j.id, j); map.setJob(j); });
    render();
  }

  conn.onreconnecting(() => setConn('reconnecting', 'Reconnecting…'));
  conn.onclose(() => setConn('offline', 'Offline'));
  conn.onreconnected(async () => { setConn('online', 'Live'); await load(); });

  renderPicked();
  try { await load(); } catch (e) { toast(e.message); }
  try { await conn.start(); setConn('online', 'Live'); } catch { setConn('offline', 'Offline'); }
})();
