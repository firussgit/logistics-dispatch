(async () => {
  await Auth.require(['Dispatcher']);
  const $ = (id) => document.getElementById(id);
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

  async function load() {
    const list = await api('/api/admin/drivers');
    $('rows').replaceChildren(...(list.length
      ? list.map((a) => el('tr', {}, el('td', { textContent: a.displayName }), el('td', { textContent: a.email }), el('td', { textContent: new Date(a.createdAt).toLocaleString() })))
      : [el('tr', {}, el('td', { colSpan: 3, textContent: 'No driver accounts yet.' }))]));
  }

  $('form').onsubmit = async (ev) => {
    ev.preventDefault();
    try {
      const a = await api('/api/admin/drivers', {
        method: 'POST',
        body: JSON.stringify({ displayName: $('name').value, email: $('email').value, password: $('password').value }),
      });
      toast(`Created ${a.displayName}`, 'info');
      $('form').reset();
      await load();
    } catch (e) { toast(e.message); }
  };

  try { await load(); } catch (e) { toast(e.message); }
})();
