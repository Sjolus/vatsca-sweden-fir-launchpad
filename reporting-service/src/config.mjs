import { resolve } from 'node:path';

export const DEFAULT_LIMITS = Object.freeze({
  autoPerDay:50000, autoBytesPerDay:50*1024*1024, supportAutoPerDay:600,
  reportsPerDay:100, supportReportsPerDay:10, supportAutoPerMinute:60, supportReportsPerMinute:3,
  maxEvents:100000, maxEventBytes:512*1024*1024, maxReports:1000, maxReportBytes:16*1024*1024,
  maxCounterRows:100000, maxBurstRows:10000, usageStopPercent:80,
  maxAggregateRows:20000, maxAggregateBytes:8*1024*1024, maxAggregateVersions:64,
  eventTtlDays:30, closedReportTtlDays:90, aggregateTtlDays:365,
  maxDbBytes:768*1024*1024, freeSpaceReserveBytes:128*1024*1024,
  sourceRequestsPerMinute:120, globalRequestsPerMinute:600, globalBytesPerMinute:8*1024*1024,
  maxSources:4096, maxConcurrent:16, requestTimeoutMs:10000,
});
export function normalizeLimits(input = {}) {
  for (const key of Object.keys(input)) if (!(key in DEFAULT_LIMITS)) throw new Error('Unknown service limit');
  const result = {...DEFAULT_LIMITS,...input};
  for (const [key,value] of Object.entries(result))
    if (!Number.isSafeInteger(value) || value <= 0 || (key !== 'freeSpaceReserveBytes' && value > DEFAULT_LIMITS[key]))
      throw new Error('Invalid service limit');
  if (result.maxDbBytes < 131072) throw new Error('Database ceiling is too small');
  if (result.eventTtlDays < 8) throw new Error('Event retention must cover the retry horizon');
  return Object.freeze(result);
}
export function readConfig(env = process.env) {
  const origin = env.PUBLIC_ORIGIN || 'https://launchpad.space.sjolus.org';
  const parsed = new URL(origin);
  if (parsed.protocol !== 'https:' || parsed.origin !== origin) throw new Error('A canonical HTTPS origin is required');
  const port = Number(env.PORT || 3000);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid listener port');
  const overrides = {};
  for (const key of Object.keys(DEFAULT_LIMITS)) {
    const name = 'LIMIT_' + key.replace(/[A-Z]/g, x=>'_'+x).toUpperCase();
    if (env[name] !== undefined) overrides[key] = Number(env[name]);
  }
  const trustedProxyCidrs=(env.TRUSTED_PROXY_CIDRS || '').split(',').map(x=>x.trim()).filter(Boolean);
  if(trustedProxyCidrs.length>16) throw new Error('Too many trusted proxy ranges');
  return {origin,port,host:'0.0.0.0',dataPath:resolve(env.DATA_PATH || './data/reporting.sqlite'),
    privacyOperator:env.PRIVACY_OPERATOR || '',privacyContact:env.PRIVACY_CONTACT || '',
    trustedProxyCidrs,ntfyEndpoint:env.NTFY_ENDPOINT || '',ntfyToken:env.NTFY_TOKEN || '',
    limits:normalizeLimits(overrides)};
}
