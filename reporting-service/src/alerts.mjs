// Optional operator notifications. Only these fixed messages can leave the service.
const messages = Object.freeze({
  storage_pressure: 'Launchpad reporting storage is approaching its limit. Review service health.',
  intake_unavailable: 'Launchpad reporting storage is unavailable. Review service health.',
  new_reports: 'New Launchpad problem reports are ready for review.',
  error_volume: 'Recent Launchpad error reports need review. Check the release and application filters.',
});

export function createAlerts({ endpoint, token, origin, now = Date.now, fetchImpl = fetch } = {}) {
  if (!endpoint) return { observe() {}, async close() {} };
  const target = new URL(endpoint), link = new URL('/backoffice/', origin).href;
  if (target.protocol !== 'https:' || target.username || target.password || target.hash || target.search || !token || token.length > 4096)
    throw new Error('Private HTTPS notification endpoint and token are required');
  const lastSent = new Map();
  let previous = null, pending = null, closed = false;
  const cooldown = 30 * 60 * 1000;

  async function send(reason) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 5000);
    try {
      const response = await fetchImpl(target, {
        method: 'POST', redirect: 'error', signal: controller.signal,
        headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'text/plain; charset=utf-8',
          Title: 'Launchpad reporting', Click: link, Priority: 'default' },
        body: messages[reason],
      });
      // Notifications are best effort, with no retry spool or response/body logging.
      await response.body?.cancel();
    } catch { /* Public intake must not depend on notifications. */ }
    finally { clearTimeout(timeout); }
  }
  return {
    observe({ health, accepted }) {
      if (closed) return;
      const clock = now();
      const prior = previous;
      previous = { errors: accepted.errors, reports: accepted.reports };
      if (pending) return;
      const reasons = [];
      if (!health.ok) reasons.push('intake_unavailable');
      else if (health.physical_bytes >= health.max_db_bytes * .8 || health.usage_paused === true) reasons.push('storage_pressure');
      if (prior && accepted.errors - prior.errors >= 20) reasons.push('error_volume');
      if (prior && accepted.reports > prior.reports) reasons.push('new_reports');
      const reason = reasons.find(value => !lastSent.has(value) || clock - lastSent.get(value) >= cooldown);
      if (!reason) return;
      // Failed sends also observe the cooldown; a broken endpoint cannot create a retry storm.
      lastSent.set(reason, clock);
      pending = send(reason).finally(() => { pending = null; });
    },
    async close() { closed = true; await pending; },
  };
}
