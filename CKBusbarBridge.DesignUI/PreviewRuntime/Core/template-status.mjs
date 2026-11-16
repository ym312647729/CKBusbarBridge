export function templateConfigurationStatus(template,configuration,auditRow,definitionSha256){
  if(!template)return {code:'uncaptured',label:'尚未采集',canAdd:false};
  if(template.representation?.mode==='deferred')return {code:'deferred',label:'按用户要求暂缓',canAdd:false};
  if(!auditRow||!definitionSha256||auditRow.definitionSha256!==definitionSha256)return {code:'unaudited',label:'待离线构造检查',canAdd:true};
  if(auditRow.configuration!==configuration.name)throw new Error('配置审计关联错误');
  if(!auditRow.ok)return {code:'unsupported',label:'特征尚未支持',canAdd:true,error:auditRow.error,source:auditRow.source};
  return {code:auditRow.volumeReviewRequired?'review':auditRow.deferred.length?'partial':'constructible',label:auditRow.volumeReviewRequired?'可构造 · 质量待查':auditRow.deferred.length?'可构造 · 含暂缓件':'可构造 · 待核验',canAdd:true,schematicCount:auditRow.schematic.length,deferredCount:auditRow.deferred.length,volumeReviewRequired:!!auditRow.volumeReviewRequired,assemblySourceState:auditRow.assemblySourceState};
}
