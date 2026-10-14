import {ENUMS,FEATURES,INVENTORY,VERSION,IntakeError} from './schema.mjs';

const DAY=86400000;
const iso=value=>new Date(value).toISOString();
const date=value=>iso(value).slice(0,10);
const invalid=()=>{throw new IntakeError(400,'invalid_request');};

// SQL performs grouping before rows leave SQLite. No raw support IDs or event
// payloads appear in the response, and each distribution has a fixed upper bound.
export function createUsageAnalytics({db,now,limits}) {
  return function usage(input={}) {
    const days=input.days ?? 7;
    if(![7,30].includes(days))invalid();
    const application=input.application || null,release=input.launchpad_version || null;
    if(application && !ENUMS.application.includes(application))invalid();
    if(release && (typeof release!=='string' || release.length>40 || !VERSION.test(release)))invalid();
    // Include today up to the server's observation time. Millisecond timestamps
    // at exactly `clock` belong to this view; accepted clock-skewed future events
    // do not. Returning IDs overlap the contiguous preceding full UTC dates;
    // this measures repeat use, not growth between equal-duration samples.
    const clock=now(),today=Math.floor(clock/DAY)*DAY,start=today-(days-1)*DAY,end=clock+1;
    const previousStart=start-days*DAY,previousEnd=start;
    const coverage=db.prepare('SELECT coverage_start FROM analytics_state WHERE singleton=1').get();
    if(!coverage)throw new IntakeError(503,'storage_unavailable',60);
    const availableSince=Math.max(coverage.coverage_start,clock-limits.eventTtlDays*DAY);
    function scope(from,to) {
      const params=[clock,from,to];
      let version='';if(release){version=' AND launchpad_version=?';params.push(release);}
      let app='';if(application){app=' WHERE application=?';params.push(application);}
      // A JOIN against the distinct cohort can make SQLite repeat the time-range
      // scan once per installation. IN materializes one bounded membership set;
      // the unfiltered view needs no cohort join at all.
      const membership=application?' WHERE support_id IN (SELECT support_id FROM cohort)':'';
      return {params,sql:`WITH base AS (
        SELECT event_id,support_id,session_id,event_name,application,launchpad_version,occurred_at,received_at
        FROM events WHERE stream='usage' AND event_name<>'inventory_snapshot' AND expires_at>? AND occurred_at>=? AND occurred_at<?${version}
      ),cohort AS (SELECT DISTINCT support_id FROM base${app}),scoped AS (
        SELECT * FROM base${membership}
      ),sessions AS (
        SELECT DISTINCT support_id,session_id FROM scoped
      )`};
    }
    const current=scope(start,end);
    const all=(suffix,extra=[])=>db.prepare(current.sql+' '+suffix).all(...current.params,...extra).map(row=>({...row}));
    const one=(suffix,extra=[])=>db.prepare(current.sql+' '+suffix).get(...current.params,...extra);
    const active=one('SELECT COUNT(*) n FROM cohort').n;
    const sessions=one('SELECT COUNT(*) n FROM sessions').n;
    const launches=one(`SELECT COUNT(*) launches,COUNT(DISTINCT support_id) installations FROM scoped WHERE event_name='application_opened'${application?' AND application=?':''}`,application?[application]:[]);
    const returningAvailable=previousStart>=availableSince;
    let returning=null;
    if(returningAvailable) {
      const previous=scope(previousStart,previousEnd);
      returning=db.prepare(`SELECT COUNT(*) n FROM (SELECT support_id FROM (${current.sql} SELECT support_id FROM cohort) INTERSECT SELECT support_id FROM (${previous.sql} SELECT support_id FROM cohort))`).get(...current.params,...previous.params).n;
    }
    const daily=new Map(Array.from({length:days},(_,i)=>{const d=date(start+i*DAY);return[d,{date:d,partial:i===days-1,active_installations:0,sessions:0,app_launches:0}];}));
    for(const row of all(",daily_sessions AS (SELECT DISTINCT strftime('%Y-%m-%d',occurred_at/1000,'unixepoch') date,support_id,session_id FROM scoped) SELECT date,COUNT(DISTINCT support_id) active_installations,COUNT(*) sessions FROM daily_sessions GROUP BY date"))Object.assign(daily.get(row.date),row);
    for(const row of all(`SELECT strftime('%Y-%m-%d',occurred_at/1000,'unixepoch') date,COUNT(*) app_launches FROM scoped WHERE event_name='application_opened'${application?' AND application=?':''} GROUP BY date`,application?[application]:[]))daily.get(row.date).app_launches=row.app_launches;
    const releases=all(`,latest AS (SELECT launchpad_version,ROW_NUMBER() OVER(PARTITION BY support_id ORDER BY occurred_at DESC,received_at DESC,event_id DESC) rn FROM scoped),counts AS (
      SELECT launchpad_version version,COUNT(*) installations FROM latest WHERE rn=1 GROUP BY launchpad_version
    ),ranked AS (SELECT *,ROW_NUMBER() OVER(ORDER BY installations DESC,version) rank FROM counts)
    SELECT CASE WHEN rank<50 THEN version ELSE 'other' END version,SUM(installations) installations FROM ranked GROUP BY CASE WHEN rank<50 THEN version ELSE 'other' END ORDER BY installations DESC,version`);
    const applications=all(`SELECT application,COUNT(*) launches,COUNT(DISTINCT support_id) installations FROM scoped WHERE event_name='application_opened'${application?' AND application=?':''} GROUP BY application ORDER BY launches DESC,application`,application?[application]:[]);
    // Fetch payload only for the matching typed observations, rather than copying
    // every retained JSON body into each temporary cohort query.
    const payload='(SELECT payload FROM events WHERE events.event_id=scoped.event_id)';
    const features=all(`SELECT json_extract(${payload},'$.feature') feature,COUNT(*) uses,COUNT(DISTINCT support_id) installations FROM scoped WHERE event_name='feature_used' GROUP BY feature ORDER BY uses DESC,feature`).filter(row=>FEATURES.includes(row.feature));
    const setupRoutes=all(`,steps AS (SELECT support_id,json_extract(${payload},'$.source') source,ROW_NUMBER() OVER(PARTITION BY support_id,json_extract(${payload},'$.operation_id') ORDER BY occurred_at DESC,received_at DESC,event_id DESC) rn FROM scoped WHERE event_name='setup_step' AND json_extract(${payload},'$.operation_id') IS NOT NULL)
      SELECT CASE WHEN source IN ('fresh','existing','preferences') THEN source ELSE 'unknown' END route,COUNT(*) attempts,COUNT(DISTINCT support_id) installations FROM steps WHERE rn=1 GROUP BY route ORDER BY attempts DESC,route`);
    const gngSources=all(`SELECT CASE WHEN json_extract(${payload},'$.source') IN ('cache','download','import') THEN json_extract(${payload},'$.source') ELSE 'unknown' END source,COUNT(*) packages,COUNT(DISTINCT support_id) installations FROM scoped WHERE event_name='gng_package_obtained' GROUP BY source ORDER BY packages DESC,source`);
    const prefs=` ,preferences AS (SELECT ${payload} payload,ROW_NUMBER() OVER(PARTITION BY support_id ORDER BY occurred_at DESC,received_at DESC,event_id DESC) rn FROM scoped WHERE event_name='preferences_snapshot') `;
    const responding=one(prefs+'SELECT COUNT(*) n FROM preferences WHERE rn=1').n;
    const display={responding_installations:responding,missing_installations:active-responding};
    for(const field of ['compact_layout','text_scale','always_on_top','theme']) {
      display[field]=all(prefs+`SELECT COALESCE(json_extract(payload,'$.${field}'),'unknown') value,COUNT(*) installations FROM preferences WHERE rn=1 GROUP BY value ORDER BY installations DESC,value`)
        .map(row=>({...row,value:['compact_layout','always_on_top'].includes(field)?row.value===1:row.value}));
    }
    const inventoryParams=[start,end,clock,...(release?[release]:[])];
    const latestInventory=`WITH ordered AS (SELECT event_id,occurred_at,ROW_NUMBER() OVER(PARTITION BY support_id ORDER BY occurred_at DESC,received_at DESC,event_id DESC) rn FROM events WHERE stream='usage' AND event_name='inventory_snapshot' AND occurred_at>=? AND occurred_at<? AND expires_at>?${release?' AND launchpad_version=?':''}),latest AS (SELECT e.payload,o.occurred_at FROM ordered o JOIN events e ON e.event_id=o.event_id WHERE rn=1),items AS (
      SELECT json_extract(j.value,'$.component') component,json_extract(j.value,'$.status') status,json_extract(j.value,'$.version') version FROM latest,json_each(latest.payload,'$.items') j
    )`;
    const invQuery=suffix=>db.prepare(latestInventory+' '+suffix).all(...inventoryParams);
    const inventoryTotals=db.prepare(latestInventory+' SELECT COUNT(*) installations,MAX(occurred_at) latest_at FROM latest').get(...inventoryParams);
    const counts=invQuery('SELECT component,status,COUNT(*) installations FROM items GROUP BY component,status');
    const versions=invQuery(`,counts AS (SELECT component,version,COUNT(*) installations FROM items WHERE status='known' GROUP BY component,version),ranked AS (SELECT *,ROW_NUMBER() OVER(PARTITION BY component ORDER BY installations DESC,version) rank FROM counts)
      SELECT component,CASE WHEN rank<50 THEN version ELSE 'other' END version,SUM(installations) installations FROM ranked GROUP BY component,CASE WHEN rank<50 THEN version ELSE 'other' END ORDER BY component,installations DESC,version`);
    const components=Object.entries(INVENTORY).filter(([component,source])=>!application || component===application || (application==='euroscope' && source==='plugin_file')).map(([component,source])=>{
      const row={component,source,known:0,unknown:0,absent:0,unavailable:0,versions:versions.filter(v=>v.component===component).map(({version,installations})=>({version,installations}))};
      for(const count of counts)if(count.component===component)row[count.status]=count.installations;
      return row;
    });
    return {window:{days,start:iso(start),end_exclusive:iso(end),as_of:iso(clock),timezone:'UTC',complete_days:false,partial_day:date(today),previous_start:iso(previousStart),previous_end_exclusive:iso(previousEnd),raw_retention_days:limits.eventTtlDays,coverage_start:iso(availableSince),coverage_complete:start>=availableSince},filters:{application,launchpad_version:release},
      cohort:{active_installations:active,sessions,app_launches:launches.launches,app_launch_installations:launches.installations,returning_installations:returning,returning_available:returningAvailable,returning_reason:returningAvailable?'available':previousStart<clock-limits.eventTtlDays*DAY?'retention':'coverage'},
      daily:[...daily.values()],releases,applications,features,setup_routes:setupRoutes,gng_sources:gngSources,display_preferences:display,
      inventory:{snapshot_installations:inventoryTotals.installations,latest_snapshot_at:inventoryTotals.latest_at===null?null:iso(inventoryTotals.latest_at),cohort:'inventory_snapshots',components}};
  };
}
