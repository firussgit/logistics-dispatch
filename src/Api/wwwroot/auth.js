// Shared sign-in helpers for every page. The session itself is an HttpOnly cookie the server manages;
// this script only asks "who am I?" and sends people to the right place.
(() => {
  const HOME = { Dispatcher: '/', Driver: '/driver.html', Customer: '/customer.html' };
  const LABEL = { Dispatcher: 'Dispatcher', Driver: 'Driver', Customer: 'Customer' };

  const hang = () => new Promise(() => {}); // stop the calling page's script while the browser navigates away

  /** The signed-in user, or null. */
  async function me() {
    try {
      const res = await fetch('/api/me');
      return res.ok ? await res.json() : null;
    } catch {
      return null;
    }
  }

  function toLogin() {
    const next = location.pathname + location.search;
    location.replace(`/login.html?next=${encodeURIComponent(next)}`);
  }

  /** Call first thing on a protected page. Redirects (and never resolves) unless the user is signed in with an allowed role. */
  async function require(roles) {
    const user = await me();
    if (!user) { toLogin(); return hang(); }
    if (roles && !roles.includes(user.role)) { location.replace(HOME[user.role] ?? '/login.html'); return hang(); }
    mount(user);
    return user;
  }

  /** Fills <span id="userbox"> in the page header: name, role and a sign-out button. */
  function mount(user) {
    const box = document.getElementById('userbox');
    if (!box) return;
    const name = Object.assign(document.createElement('span'), { className: 'user-name', textContent: user.displayName });
    const role = Object.assign(document.createElement('span'), { className: `role-chip role-${user.role.toLowerCase()}`, textContent: LABEL[user.role] ?? user.role });
    const out = Object.assign(document.createElement('button'), { className: 'signout', textContent: 'Sign out', onclick: logout });
    box.replaceChildren(name, role, out);
  }

  async function logout() {
    try { await fetch('/api/auth/logout', { method: 'POST' }); } catch { /* going to the login page either way */ }
    location.replace('/login.html');
  }

  /** Call when an API request comes back 401 (session expired or signed out in another tab). */
  function expired() { toLogin(); }

  /** Only same-site relative paths are acceptable redirect targets (no open redirect via ?next=). */
  function safeNext(next) {
    return typeof next === 'string' && /^\/(?![/\\])/.test(next) ? next : null;
  }

  window.Auth = { me, require, logout, expired, safeNext, HOME };
})();
