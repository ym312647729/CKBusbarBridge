import {DefinitionError} from './expressions.mjs';
export const clearanceVersion='physical-hanger-clearance/1';
export function clearanceVersionFor(category='hanger'){if(category==='hanger')return clearanceVersion;if(category==='copper')return 'physical-copper-clearance/1';throw new DefinitionError('净距检查范围无效');}
export function validateMinimumGap(value){if(!Number.isFinite(value)||value<0)throw new DefinitionError('最小净距必须为不小于零的有限毫米数');return value;}
export function clearanceInputSignature(project,minimumGapMm,category='hanger'){
  validateMinimumGap(minimumGapMm);
  const sorted=value=>Object.entries(value??{}).sort(([left],[right])=>left.localeCompare(right));
  return JSON.stringify([clearanceVersionFor(category),minimumGapMm,project.scenario,project.cabinetInputs??{},project.objects.map(object=>[object.id,object.category,object.parentId,object.templatePath,object.configuration,object.sourceSha256,sorted(object.parameters),sorted(object.suppression),object.transform,object.hangerDimensions??null,object.hangerBinding??null,object.freeCopper??null,object.originalCopper??null,object.structurePlanInput??null,object.phase??null,object.cabinetBinding??null,object.cabinetSceneInput??null]),project.routes??[],project.supportPlans??[],project.hangerPlans?.map(draft=>[draft.id,draft.input,draft.plans])??[],project.copperLaps?.map(lap=>[lap.id,lap.input])??[],project.settings.arcSegments,project.settings.complexArcSegments??null,project.settings.chordToleranceMm]);
}
export function validatedClearanceState(project,category='hanger'){
  const state=project[category+'Clearance']??{minimumGapMm:null,report:null};
  if(!state||typeof state!=='object')throw new DefinitionError('净距检查记录无效');
  if(state.minimumGapMm!==null)validateMinimumGap(state.minimumGapMm);
  const report=state.report;
  if(!report)return {minimumGapMm:state.minimumGapMm,report:null};
  const nonnegative=value=>Number.isFinite(value)&&value>=0;
  const valid=state.minimumGapMm!==null&&report.minimumGapMm===state.minimumGapMm&&report.version===clearanceVersionFor(category)&&report.inputSignature===clearanceInputSignature(project,state.minimumGapMm,category)&&nonnegative(report.totalMs)&&Number.isInteger(report.conflicts)&&report.conflicts>=0&&typeof report.scope==='string'&&typeof report.approximation==='string'&&Array.isArray(report.pairs)&&report.pairs.every(pair=>typeof (category==='hanger'?pair?.hangerId:pair?.subjectId)==='string'&&typeof pair.objectId==='string'&&typeof (category==='hanger'?pair.hangerName:pair.subjectName)==='string'&&typeof pair.objectName==='string'&&(pair.status==='unavailable'||['overlap','contact','numerical-contact','below-minimum','clear'].includes(pair.status)&&nonnegative(pair.gapMm)&&nonnegative(pair.overlapVolumeMm3)))&&Array.isArray(report.blockers)&&report.blockers.every(row=>typeof row?.message==='string')&&['incomplete','conflicts','no-conflicts-in-discretization'].includes(report.conclusion);
  return {minimumGapMm:state.minimumGapMm,report:valid?structuredClone(report):null};
}



