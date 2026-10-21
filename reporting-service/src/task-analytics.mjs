// Correlate only consented usage records. Error reporting has a different sample
// and must never supply the numerator of a task completion measure.
export function taskAnalytics({one,payload,application}) {
  const params=application?[application]:[];
  const typed=`,task_observations AS MATERIALIZED (
    SELECT event_id,support_id,application,event_name,occurred_at,received_at,
      json_extract(${payload},'$.operation_id','$.operation','$.outcome','$.duration_bucket','$.reason') details
    FROM scoped WHERE event_name IN ('operation_started','operation_finished')${application?' AND application=?':''}
  ),task_events AS MATERIALIZED (
    SELECT event_id,support_id,application,event_name,occurred_at,received_at,
      json_extract(details,'$[0]') operation_id,json_extract(details,'$[1]') operation,
      json_extract(details,'$[2]') outcome,json_extract(details,'$[3]') duration_bucket,
      json_extract(details,'$[4]') reason FROM task_observations
  )`;
  const attempts=typed+`,ordered_tasks AS (
    SELECT *,ROW_NUMBER() OVER(PARTITION BY support_id,operation_id,application,operation,event_name
      ORDER BY occurred_at DESC,received_at DESC,event_id DESC) rn
    FROM task_events WHERE operation_id IS NOT NULL AND application IS NOT NULL AND operation IS NOT NULL
  ),paired_tasks AS (
    SELECT support_id,operation_id,application,operation,
      MIN(CASE WHEN event_name='operation_started' THEN occurred_at END) started_at,
      MAX(CASE WHEN event_name='operation_finished' AND rn=1 THEN occurred_at END) finished_at,
      MAX(CASE WHEN event_name='operation_finished' AND rn=1 THEN outcome END) outcome,
      MAX(CASE WHEN event_name='operation_finished' AND rn=1 THEN duration_bucket END) duration_bucket,
      MAX(CASE WHEN event_name='operation_finished' AND rn=1 THEN reason END) reason
    FROM ordered_tasks GROUP BY support_id,operation_id,application,operation
  ),tasks AS MATERIALIZED (
    SELECT *,CASE WHEN started_at IS NULL THEN 'unmatched'
      WHEN finished_at IS NULL OR finished_at<started_at THEN 'incomplete'
      ELSE COALESCE(outcome,'unknown') END result,
      CASE WHEN finished_at>=started_at THEN 1 ELSE 0 END matched
    FROM paired_tasks
  )`;
  const summary=`SELECT
    COUNT(CASE WHEN started_at IS NOT NULL THEN 1 END) started,
    COUNT(DISTINCT CASE WHEN started_at IS NOT NULL THEN support_id END) installations,
    COALESCE(SUM(matched),0) matched,
    COUNT(CASE WHEN result='incomplete' THEN 1 END) incomplete,
    COUNT(CASE WHEN finished_at IS NOT NULL AND (started_at IS NULL OR finished_at<started_at) THEN 1 END) unmatched_finishes,
    (SELECT COUNT(*) FROM task_events WHERE operation_id IS NULL OR application IS NULL OR operation IS NULL) unlinked_records
    FROM tasks`;
  const outcomes=`SELECT result outcome,COUNT(*) attempts FROM tasks WHERE started_at IS NOT NULL GROUP BY result ORDER BY attempts DESC,result`;
  const byTask=`SELECT application,operation,COUNT(*) started,COUNT(DISTINCT support_id) installations,
    SUM(matched) matched,
    COUNT(CASE WHEN result='completed' THEN 1 END) completed,
    COUNT(CASE WHEN result='partial' THEN 1 END) partial,
    COUNT(CASE WHEN result='blocked' THEN 1 END) blocked,
    COUNT(CASE WHEN result='failed' THEN 1 END) failed,
    COUNT(CASE WHEN result='cancelled' THEN 1 END) cancelled,
    COUNT(CASE WHEN result='unknown' THEN 1 END) unknown,
    COUNT(CASE WHEN result='incomplete' THEN 1 END) incomplete
    FROM tasks WHERE started_at IS NOT NULL GROUP BY application,operation ORDER BY started DESC,application,operation`;
  const durations=`SELECT duration_bucket,COUNT(*) attempts FROM tasks WHERE matched=1 AND duration_bucket IS NOT NULL GROUP BY duration_bucket
    ORDER BY CASE duration_bucket WHEN 'under_1s' THEN 0 WHEN 'under_10s' THEN 1 WHEN 'under_1m' THEN 2 WHEN 'under_5m' THEN 3 ELSE 4 END`;
  const reasons=`SELECT reason,result outcome,COUNT(*) attempts FROM tasks WHERE matched=1 AND reason IS NOT NULL GROUP BY reason,result ORDER BY attempts DESC,reason,result`;
  // Materialize correlation once and return only the bounded grouped results.
  // These field lists and SQL fragments are constants, never request input.
  const object=fields=>`json_object(${fields.map(key=>`'${key}',${key}`).join(',')})`;
  const list=(query,fields)=>`json((SELECT json_group_array(${object(fields)}) FROM (${query})))`;
  const result=one(attempts+` SELECT json_object(
    'summary',json((SELECT ${object(['started','installations','matched','incomplete','unmatched_finishes','unlinked_records'])} FROM (${summary}))),
    'outcomes',${list(outcomes,['outcome','attempts'])},
    'by_task',${list(byTask,['application','operation','started','installations','matched','completed','partial','blocked','failed','cancelled','unknown','incomplete'])},
    'durations',${list(durations,['duration_bucket','attempts'])},
    'reasons',${list(reasons,['reason','outcome','attempts'])}) result`,params);
  return JSON.parse(result.result);
}
