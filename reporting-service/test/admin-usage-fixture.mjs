// Synthetic observations for rendering tests. These are not observed release statistics.
export function usageFixture() {
  const components = [
    ['euroscope', 'configured_app', 28, 0, 1, 1, [['3.2.3.2', 24], ['3.2.9.0', 4]]],
    ['gng', 'package_metadata', 25, 1, 3, 1, [['2610.1.3', 19], ['2609.1.2', 6]]],
    ['trackaudio', 'configured_app', 27, 0, 2, 1, [['1.4.0', 23], ['1.3.3', 4]]],
    ['vacs', 'configured_app', 23, 0, 6, 1, [['2.8.0', 23]]],
    ['vatis', 'configured_app', 22, 0, 7, 1, [['4.1.0-beta.19', 18], ['4.1.0-beta.18', 4]]],
    ['vatefs', 'registered_msi', 15, 0, 14, 1, [['0.8.1', 10], ['0.8.0', 5]]],
    ['topsky', 'plugin_file', 18, 8, 3, 1, [['2.3.4', 14], ['2.3.3', 4]]],
    ['rdf', 'plugin_file', 12, 6, 11, 1, [['1.2.0', 12]]],
    ['vatefs_plugin', 'plugin_file', 0, 14, 15, 1, []],
    ['afv_euroscope_bridge', 'plugin_file', 0, 2, 27, 1, []],
  ].map(([component, source, known, unknown, absent, unavailable, versions]) => ({ component, source, known, unknown, absent, unavailable, versions: versions.map(([version, installations]) => ({version, installations})) }));
  return {
    window: {days:7,start:'2026-10-08T00:00:00.000Z',end_exclusive:'2026-10-14T14:30:00.001Z',as_of:'2026-10-14T14:30:00.000Z',timezone:'UTC',complete_days:false,partial_day:'2026-10-14',previous_start:'2026-10-01T00:00:00.000Z',previous_end_exclusive:'2026-10-08T00:00:00.000Z',raw_retention_days:30,coverage_start:'2026-09-20T00:00:00.000Z',coverage_complete:true},
    filters:{application:null,launchpad_version:null},
    cohort:{active_installations:42,sessions:96,app_launches:148,app_launch_installations:35,returning_installations:27,returning_available:true,returning_reason:'available'},
    daily:[8,10,12,9,13,11,14].map((active_installations,index)=>({date:`2026-10-${String(index+8).padStart(2,'0')}`,partial:index===6,active_installations,sessions:[12,13,13,12,16,14,16][index],app_launches:[18,19,22,20,25,19,25][index]})),
    releases:[{version:'2.2.0',installations:31},{version:'2.1.0',installations:8},{version:'2.0.2',installations:3}],
    applications:[['euroscope',46,32],['trackaudio',39,29],['vacs',22,20],['vatis',18,16],['vatefs',14,12],['vatiris',9,9]].map(([application,launches,installations])=>({application,launches,installations})),
    features:[['check_updates',51,28],['gng_package_review',18,14],['controller_profile_review',15,12],['install_or_update',14,11],['setup_guide',8,8],['gng_cleanup_review',5,4]].map(([feature,uses,installations])=>({feature,uses,installations})),
    setup_routes:[{route:'existing',attempts:5,installations:5},{route:'fresh',attempts:3,installations:3}],
    gng_sources:[{source:'cache',packages:10,installations:9},{source:'download',packages:6,installations:5},{source:'import',packages:2,installations:2}],
    display_preferences:{responding_installations:40,missing_installations:2,compact_layout:[{value:true,installations:29},{value:false,installations:11}],text_scale:[{value:'standard',installations:31},{value:'larger',installations:7},{value:'largest',installations:2}],always_on_top:[{value:true,installations:16},{value:false,installations:24}],theme:[{value:'dark',installations:35},{value:'light',installations:5}]},
    inventory:{snapshot_installations:30,latest_snapshot_at:'2026-10-13T20:00:00.000Z',cohort:'inventory_snapshots',components},
  };
}
export function emptyUsageFixture() {
  const data=usageFixture(); data.window.coverage_complete=false;
  data.cohort={active_installations:0,sessions:0,app_launches:0,app_launch_installations:0,returning_installations:null,returning_available:false,returning_reason:'coverage'};
  for(const key of ['daily','releases','applications','features','setup_routes','gng_sources']) data[key]=[];
  data.display_preferences={responding_installations:0,missing_installations:0};
  data.inventory={snapshot_installations:0,latest_snapshot_at:null,cohort:'inventory_snapshots',components:[]};
  return data;
}
