import {DefinitionError} from './expressions.mjs';
import {planFreeCopper} from './free-copper.mjs';
import {copperDimensionUpdate} from './original-copper.mjs';
import {originalCabinetScenario,createOriginalCabinetInput,editOriginalCabinetUiInput} from './original-cabinet.mjs';
import {originalJointField} from './original-joint-design.mjs';

export const copperDimensionFields=[['widthMm','排宽'],['thicknessMm','板厚'],['flatRadiusMm','平弯内半径'],['edgeRadiusMm','立弯内半径']];
export const cabinetFieldGroup=field=>field.tabs.join(' / ')||'整体参数';
export function insulatorDimensionFields(draft){
  const input=draft.input,fields=[['bracketLength','支架长度'],['barThickness','排厚'],['rootCount','单相根数'],['nominalAssemblyHeight','原落底总高']];
  if(input.layoutMode!=='individual-groups')fields.push(['phaseSpacing','相间中心距'],[input.layoutMode==='existing-copper'?'offset':'fixedHoleSpacing',input.layoutMode==='existing-copper'?'铜排横移量':'接口固定孔间距']);
  for(let group=1;group<=3;group++){fields.push(['bodyHeight'+group,'第'+group+'组本体高度']);if(input.layoutMode==='individual-groups')fields.push(['groupLocalZ'+group,'第'+group+'组局部Z']);}
  return fields.map(([name,label])=>({name,label,value:name.startsWith('bodyHeight')?input.bodyHeights[Number(name.slice(-1))-1]:name.startsWith('groupLocalZ')?input.groupLocalZ[Number(name.slice(-1))-1]:input[name]}));
}
export function cabinetFieldState(record,scenario,field){
  const values=record.values,hole=/^((?:A|B)?Cabinet\.(?:FrontHole|BackHole))\./.exec(field.path)?.[1];
  const mode=values['General.CabinetTopOpeningMode'];
  let visible=!(scenario==='side-to-side'&&(field.path.includes('.Base1.')&&mode!=='LeftRight'||field.path.includes('.Base2.')&&mode!=='FrontRear'));
  visible&&=!/\.L[12]$/.test(field.path);
  for(const condition of field.conditions){
    if(condition.visibility==='Collapsed'){
      // These panels are opened by the original window's code behind at height > 1400.
      if(condition.sourceName?.endsWith('ExtensionBasePanel'))visible&&=!!hole&&values[hole+'.BaseHeight']>1400;
      else visible=false;
    }
  }
  return {visible,editable:visible&&!field.readOnly&&(!hole||field.path.endsWith('.IsEnabled')||!!values[hole+'.IsEnabled'])};
}
export function copperGroupMembers(project,objectId){
  const selected=project.objects.find(object=>object.id===objectId);
  if(selected?.kind!=='free-copper')throw new DefinitionError('此对象尚未关联界面业务参数组，不能编辑零件尺寸');
  if(!selected.copperGroupId)return [selected];
  const members=project.objects.filter(object=>object.copperGroupId===selected.copperGroupId);
  if(members.some(object=>object.kind!=='free-copper'))throw new DefinitionError('铜排组包含非铜排对象');
  return members;
}
export function objectDimensionBinding(project,objectId,field){
  const object=project?.objects.find(object=>object.id===objectId);
  if(object?.originalDesignBinding){const mapped={'frame.length':'Length','frame.width':'Width','frame.height':'Height'}[field]??field,binding={kind:'original-joint',rowId:object.originalDesignBinding.rowId,field:mapped};try{return originalJointField(project,binding)?binding:null;}catch{return null;}}
  if(object?.structureSupportBinding&&['General.InsulatorHeight',project.scenario==='face-to-face'?'General.PhaseSpacing':'General.ExposedBusbarPhaseSpacing'].includes(field)){const input=originalCabinetScenario(project.scenario).fields.find(input=>input.path===field);if(input)return {kind:'cabinet',scenario:project.scenario,group:cabinetFieldGroup(input),field};}
  if(object?.baseBinding?.scenario===project.scenario&&object.structurePlanInput.fields.some(input=>input.name===field)){const input=originalCabinetScenario(project.scenario).fields.find(input=>input.path===field);if(input)return {kind:'cabinet',scenario:project.scenario,group:cabinetFieldGroup(input),field};}
  if(object?.frameBinding&&/^frame\.(?:width|length|height)$/.test(field)){const position=project.frameDesign?.plans.find(plan=>plan.positionKey===object.frameBinding.positionKey);if(position)return {kind:'frame',objectId,sourceName:position.model.sourceName,field};}
  if(object?.insulatorBinding){const draft=project.supportPlans.find(plan=>plan.id===object.insulatorBinding.planId);if(draft&&insulatorDimensionFields(draft).some(input=>input.name===field))return {kind:'insulator',objectId,planId:draft.id,field};}
  if(object?.cabinetBinding&&object.cabinetBinding.scenario===project.scenario){
    const wall=project.scenario==='face-to-wall';
    const placementField=wall?/^General\.(CabinetToWallDistance|WallCabinetEccentricity)$/:/^General\.(CabinetSpacing|CabinetEccentricity)$/;
    if(placementField.test(field)||/^(?:[AB]Cabinet|Cabinet)\.(CabinetHeight|CabinetDepth|CabinetWidth)$/.test(field)||wall&&/^General\.(?:WallBushingTerminal\.(?:Diameter|C(?:[1-9]|10))|WallHole(?:Width|Height))$/.test(field)){
      const input=originalCabinetScenario(project.scenario).fields.find(candidate=>candidate.path===field);
      if(input)return {kind:'cabinet',scenario:project.scenario,group:cabinetFieldGroup(input),field};
    }
  }
  return object?.kind==='free-copper'&&copperDimensionFields.some(([name])=>name===field)?{kind:'copper',objectId,groupId:object.copperGroupId??object.id,field}:null;
}
export function applyCopperGroupDimensions(project,objectId,patch){
  const members=copperGroupMembers(project,objectId),allowed=new Set(copperDimensionFields.map(([name])=>name));
  if(!patch||Object.keys(patch).some(name=>!allowed.has(name)||!Number.isFinite(patch[name])))throw new DefinitionError('只能修改界面已有的铜排尺寸');
  const updates=members.map(object=>({object,data:copperDimensionUpdate(object,patch)}));
  // Validate every member before changing any member. Preserve each phase's path and holes.
  for(const {object,data}of updates){Object.assign(object,data);for(const name of allowed)delete object.parameters[name];}
  return members.map(object=>object.id);
}
export function prepareBusinessDimensionEdit(project,binding){
  if(binding?.kind==='original-joint'){
    const {row}=originalJointField(project,binding);return {binding:{kind:'original-joint',rowId:row.id,field:binding.field},projectId:project.id,revision:project.revision,title:row.name,scope:row.memberIds.length?row.memberIds:[row.name],fields:row.fields.filter(field=>!field.readOnly&&Number.isFinite(field.value)).map(field=>({name:field.field,label:field.label,value:field.value}))};
  }
  if(binding?.kind==='frame'){
    const canonical=objectDimensionBinding(project,binding.objectId,binding.field),position=project.frameDesign?.plans.find(plan=>plan.model.sourceName===canonical?.sourceName);if(!canonical||canonical.sourceName!==binding.sourceName||!position)throw new DefinitionError('基架尺寸不属于当前同名业务组');
    const model=position.model,input=project.frameDesign.inputs.find(row=>row.sourceName.trim().toUpperCase()===model.sourceName.toUpperCase());return {binding:canonical,projectId:project.id,revision:project.revision,title:'基架组 · '+model.sourceName,scope:project.frameDesign.plans.filter(plan=>plan.model.sourceName===model.sourceName).map(plan=>plan.positionKey),fields:[['width','宽度'],['length','长度'],['height','高度']].map(([name,label])=>({name:'frame.'+name,label,value:input[name]}))};
  }
  if(binding?.kind==='insulator'){
    const canonical=objectDimensionBinding(project,binding.objectId,binding.field),draft=project.supportPlans.find(plan=>plan.id===binding.planId);if(!draft||canonical?.planId!==draft.id)throw new DefinitionError('绝缘子尺寸不属于当前布局组');
    return {binding:canonical,projectId:project.id,revision:project.revision,title:'绝缘子布局组',scope:[draft.name,'三组位置、高度、支架及垫块按原规则联动'],fields:insulatorDimensionFields(draft)};
  }
  if(binding?.kind==='copper'){
    const canonical=objectDimensionBinding(project,binding.objectId,binding.field);
    if(!canonical||canonical.groupId!==binding.groupId)throw new DefinitionError('尺寸不属于当前铜排组');
    const members=copperGroupMembers(project,binding.objectId),selected=members.find(object=>object.id===binding.objectId),data=planFreeCopper(selected.freeCopper,selected.parameters).data;
    return {binding:canonical,projectId:project.id,revision:project.revision,title:members.length>1?'铜排组尺寸':'自由铜排尺寸',scope:members.map(object=>object.name),fields:copperDimensionFields.map(([name,label])=>({name,label,value:data[name],mixed:members.some(object=>planFreeCopper(object.freeCopper,object.parameters).data[name]!==data[name])}))};
  }
  if(binding?.kind==='cabinet'&&binding.scenario===project.scenario){
    const definition=originalCabinetScenario(project.scenario),record=project.cabinetInputs?.[project.scenario]??createOriginalCabinetInput(project.scenario);
    const fields=definition.fields.filter(field=>cabinetFieldGroup(field)===binding.group&&typeof field.defaultValue==='number'&&cabinetFieldState(record,project.scenario,field).editable);
    if(!fields.some(field=>field.path===binding.field))throw new DefinitionError('尺寸不是当前组可输入的原项目参数');
    return {binding:{kind:'cabinet',scenario:project.scenario,group:binding.group,field:binding.field},projectId:project.id,revision:project.revision,title:binding.group,scope:['按原项目参数组联动'],fields:fields.map(field=>({name:field.path,label:field.label,value:record.values[field.path]}))};
  }
  throw new DefinitionError('尺寸没有对应的界面业务参数组');
}
export function applyBusinessDimensionEdit(project,request){
  if(request?.projectId!==project.id||request?.revision!==project.revision)throw new DefinitionError('项目或参数已更新，请重新双击尺寸后修改');
  const context=prepareBusinessDimensionEdit(project,request.binding),patch=request.values;
  if(!patch||Array.isArray(patch)||!Object.keys(patch).length||Object.keys(patch).some(name=>!context.fields.some(field=>field.name===name)||!Number.isFinite(patch[name])))throw new DefinitionError('提交中含有界面未开放的尺寸');
  if(context.binding.kind==='copper')return applyCopperGroupDimensions(project,context.binding.objectId,patch);
  if(context.binding.kind==='frame'){
    const name=context.binding.sourceName.trim().toUpperCase();
    for(const input of project.frameDesign.inputs.filter(input=>input.sourceName.trim().toUpperCase()===name))for(const [field,value]of Object.entries(patch)){const property=field.slice('frame.'.length);input[property]=value;if(property==='width'&&input.smallOffset)input.smallOffset.frameWidth=value;}
    return project.objects.filter(object=>object.frameBinding&&project.frameDesign.plans.find(plan=>plan.positionKey===object.frameBinding.positionKey)?.model.sourceName.toUpperCase()===name).map(object=>object.id);
  }
  if(context.binding.kind==='insulator'){
    const draft=project.supportPlans.find(plan=>plan.id===context.binding.planId);for(const [name,value]of Object.entries(patch)){if(name.startsWith('bodyHeight'))draft.input.bodyHeights[Number(name.slice(-1))-1]=value;else if(name.startsWith('groupLocalZ'))draft.input.groupLocalZ[Number(name.slice(-1))-1]=value;else draft.input[name]=value;}return project.objects.filter(object=>object.insulatorBinding?.planId===draft.id).map(object=>object.id);
  }
  let record=project.cabinetInputs?.[project.scenario]??createOriginalCabinetInput(project.scenario);
  for(const [name,value]of Object.entries(patch))record=editOriginalCabinetUiInput(record,project.scenario,name,value);
  project.cabinetInputs??={};project.cabinetInputs[project.scenario]=record;
  return project.objects.filter(object=>object.cabinetBinding?.scenario===project.scenario).map(object=>object.id);
}
