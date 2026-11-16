import {DefinitionError} from './expressions.mjs';

// Ported from UniversalInsulatorLayout, UniversalInsulatorDrivePlan and BridgeInsulatorMountPlan.
export const insulatorRules={version:'dq-universal-insulator/1',toleranceMm:.01,minimumBodyHeightMm:120.627174484597,sideToSideBase1DatumOffsetMm:14};
export const groupDistanceNames=['第一组绝缘子距边距离','第二组绝缘子距边距离','第三组绝缘子距边距离'];
const finite=(value,label)=>{if(typeof value!=='number'||!Number.isFinite(value))throw new DefinitionError(label+'必须是有限数值');return value;};
const positive=(value,label)=>{finite(value,label);if(value<=0)throw new DefinitionError(label+'必须大于零');return value;};
function triple(values,label){if(!Array.isArray(values)||values.length!==3)throw new DefinitionError(label+'需要三个值');return values.map(value=>finite(value,label));}

export function universalInsulatorLayout(bracketLength,groupLocalZ){
  positive(bracketLength,'支架长度');const positions=triple(groupLocalZ,'绝缘子组局部位置'),distances=positions.map(position=>bracketLength/2-position);
  if(distances.some(distance=>distance<-.01||distance>bracketLength+.01))throw new DefinitionError('铜排对应的绝缘子组位置超出支架长度');
  return {bracketLength,groupLocalZ:[...positions],distances,offset:bracketLength/2-distances[1],variables:Object.fromEntries([['支架长度',bracketLength],...groupDistanceNames.map((name,index)=>[name,distances[index]])])};
}
export function insulatorPhaseSpacing(layout){const first=layout.distances[1]-layout.distances[0],second=layout.distances[2]-layout.distances[1];if(first<=0||Math.abs(first-second)>.01)throw new DefinitionError('通用绝缘子三组位置不能用单一相间距表达');return first;}
export function validateInsulatorDriveDimensions(layout,bracketLength,phaseSpacing){finite(bracketLength,'支架长度');finite(phaseSpacing,'相间距');if(Math.abs(bracketLength-layout.bracketLength)>.01||Math.abs(phaseSpacing-insulatorPhaseSpacing(layout))>.01)throw new DefinitionError('绝缘子距边布局与本次支架长度或相间距不一致');}
export function layoutFromCopper(bracketLength,phaseSpacing,offset){positive(phaseSpacing,'相间中心距');finite(offset,'铜排横移量');return universalInsulatorLayout(bracketLength,[phaseSpacing+offset,offset,-phaseSpacing+offset]);}
export function layoutFromSideToSideBase1(bracketLength,phaseSpacing,fixedHoleSpacing){positive(fixedHoleSpacing,'接口固定孔间距');return layoutFromCopper(bracketLength,phaseSpacing,bracketLength/2-(fixedHoleSpacing/2+14));}

export function universalInsulatorDrivePlan(layout,{useLBracket,bodyHeight,barThickness,rootCount,suppressedGroups=[]}){
  positive(bodyHeight,'绝缘子本体高度');positive(barThickness,'排厚');
  if(typeof useLBracket!=='boolean')throw new DefinitionError('支架类型缺失');
  if(!Number.isInteger(rootCount)||rootCount<1||rootCount>4)throw new DefinitionError('单相根数必须为1至4');
  if(!Array.isArray(suppressedGroups))throw new DefinitionError('压缩组标识无效');
  const suppressed=[...new Set(suppressedGroups)].sort((left,right)=>left-right);
  if(suppressed.some(index=>!Number.isInteger(index)||index<1||index>3)||suppressed.length===3)throw new DefinitionError('通用绝缘子必须保留至少一个有效组');
  const useStackedPads=Math.abs(barThickness-12)<.01;
  return {layout:structuredClone(layout),useLBracket,bodyHeight,barThickness,rootCount,useStackedPads,padThickness:useStackedPads?6:barThickness,enabledPadRoles:[1,3,2].slice(0,rootCount-1),suppressedGroups:suppressed};
}
export function insulatorMountPlan({transverse,bodyHeights,phases,nominalAssemblyHeight,isFloorStanding}){
  const positions=triple(transverse,'逐相位置'),heights=triple(bodyHeights,'逐相本体高度');finite(nominalAssemblyHeight,'原落底总高');
  if(!Array.isArray(phases)||phases.length!==3||typeof isFloorStanding!=='boolean')throw new DefinitionError('逐相标识或安装方式无效');
  const active=heights.flatMap((height,index)=>height>0?[index]:[]),activePhases=active.map(index=>phases[index]);
  if(nominalAssemblyHeight<=25||heights.some(height=>height!==0&&height<=insulatorRules.minimumBodyHeightMm)||!active.length||activePhases.some(phase=>!['A','B','C'].includes(phase))||new Set(activePhases).size!==active.length||isFloorStanding&&active.length!==1)throw new DefinitionError('换相安装需要有效的逐相位置、高度和原落底总高');
  return {transverse:[...positions],bodyHeights:[...heights],phases:[...phases],nominalAssemblyHeight,isFloorStanding};
}
export function createInsulatorDraft(input){
  let layout;
  if(input.layoutMode==='existing-copper')layout=layoutFromCopper(input.bracketLength,input.phaseSpacing,input.offset);
  else if(input.layoutMode==='side-to-side-base1')layout=layoutFromSideToSideBase1(input.bracketLength,input.phaseSpacing,input.fixedHoleSpacing);
  else if(input.layoutMode==='individual-groups')layout=universalInsulatorLayout(input.bracketLength,input.groupLocalZ);
  else throw new DefinitionError('绝缘子布局来源无效');
  const mount=insulatorMountPlan({...input,transverse:layout.groupLocalZ}),suppressedGroups=mount.bodyHeights.flatMap((height,index)=>height===0?[index+1]:[]);
  const groups=mount.bodyHeights.map((height,index)=>({group:index+1,phase:mount.phases[index],localZ:layout.groupLocalZ[index],distance:layout.distances[index],active:height>0,drive:height>0?universalInsulatorDrivePlan(layout,{...input,bodyHeight:height,suppressedGroups}):null}));
  return {id:crypto.randomUUID(),name:input.name||'通用绝缘子布局',kind:'universal-insulator-draft',status:'planning',ruleVersion:insulatorRules.version,input:structuredClone(input),layout,mount,groups,pending:['真实模板各组尺寸与配合驱动','垫块实例安装','逐相铜排净距校验']};
}
export function validateInsulatorDraft(draft){if(draft?.kind!=='universal-insulator-draft'||typeof draft.id!=='string'||!draft.id)throw new DefinitionError('绝缘子规划类型或标识无效');return {...createInsulatorDraft(draft.input),id:draft.id,name:draft.name||draft.input.name||'通用绝缘子布局'};}
