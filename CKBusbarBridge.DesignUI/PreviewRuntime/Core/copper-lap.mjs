import {DefinitionError} from './expressions.mjs';
import {planFreeCopper} from './free-copper.mjs';
import {inverseRigidMatrix,transformPoint} from './geometry.mjs';
import {multiplyMatrix} from './feature-patterns.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';

const dot=(a,b)=>a.reduce((s,v,i)=>s+v*b[i],0),add=(a,b)=>a.map((v,i)=>v+b[i]),scale=(a,s)=>a.map(v=>v*s),cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
const vectorToWorld=(v,m)=>[0,1,2].map(r=>m[r]*v[0]+m[r+4]*v[1]+m[r+8]*v[2]);
const basis=(d,w,t,p)=>[...d,0,...w,0,...t,0,...p,1];
export const copperLapVersion='copper-lap/1';
function selectedEnd(object,end){
  if(object?.kind!=='free-copper')throw new DefinitionError('搭接端头需要自由绘制铜排');
  if(!['start','end'].includes(end))throw new DefinitionError('请选择铜排起点或终点');
  const plan=planFreeCopper(object.freeCopper,object.parameters),segment=end==='start'?plan.segments[0]:plan.segments.at(-1),point=end==='start'?plan.data.points[0]:plan.data.points.at(-1);
  return {object,plan,segment,point,end};
}
function validatedInput(input){
  if(!input?.fixedObjectId||!input.movingObjectId||input.fixedObjectId===input.movingObjectId)throw new DefinitionError('搭接需要两根不同的铜排');
  if(!Number.isFinite(input.overlapLengthMm)||input.overlapLengthMm<=0||!Number.isFinite(input.acrossOffsetMm)||![-1,1].includes(input.side)||typeof input.reverseFace!=='boolean')throw new DefinitionError('搭接长度、宽向偏移或贴合面无效');
  if(!Array.isArray(input.holes)||!input.holes.length||input.holes.length>64)throw new DefinitionError('请录入1至64个成对搭接孔');
  for(const hole of input.holes)if(!['fromFixedEndMm','acrossMm','diameterMm','slotLengthMm'].every(key=>Number.isFinite(hole?.[key]))||hole.diameterMm<=0||hole.slotLengthMm<hole.diameterMm||hole.fromFixedEndMm-hole.slotLengthMm/2<=0||hole.fromFixedEndMm+hole.slotLengthMm/2>=input.overlapLengthMm)throw new DefinitionError('搭接孔必须完整位于搭接长度内，孔长不能小于孔径');
  return structuredClone(input);
}
// One explicit placement transaction. Later changes are re-evaluated from the
// current endpoint frames; stale joins are reported until the user reapplies them.
export function calculateCopperLap(objects,rawInput,lapId){
  const input=validatedInput(rawInput);if(typeof lapId!=='string'||!lapId)throw new DefinitionError('搭接标识缺失');
  const working=structuredClone(objects),fixed=working.find(o=>o.id===input.fixedObjectId),moving=working.find(o=>o.id===input.movingObjectId);
  if(!fixed||!moving)throw new DefinitionError('搭接引用的铜排已不存在');
  validateInstallationMatrix(fixed.transform);validateInstallationMatrix(moving.transform);
  for(const object of [fixed,moving])if(object.freeCopper)object.freeCopper.holes=(object.freeCopper.holes??[]).filter(h=>h.copperLapId!==lapId);
  const first=selectedEnd(fixed,input.fixedEnd),second=selectedEnd(moving,input.movingEnd);
  if(fixed.phase&&moving.phase&&fixed.phase!==moving.phase)throw new DefinitionError('不同相的铜排不能建立导电搭接');
  if(input.overlapLengthMm>Math.min(first.segment.lengthMm,second.segment.lengthMm)+1e-6)throw new DefinitionError('搭接长度超出端头可用直段，不能进入折弯');
  const fixedWorldPoint=transformPoint(first.point,fixed.transform),worldWidth=vectorToWorld(first.segment.width,fixed.transform),worldThickness=vectorToWorld(first.segment.thickness,fixed.transform),towardBody=scale(vectorToWorld(first.segment.direction,fixed.transform),input.fixedEnd==='start'?1:-1);
  const targetDirection=scale(towardBody,input.movingEnd==='start'?-1:1),targetThickness=scale(worldThickness,input.reverseFace?-1:1),targetWidth=cross(targetThickness,targetDirection);
  const targetPoint=add(add(add(fixedWorldPoint,scale(towardBody,input.overlapLengthMm)),scale(worldWidth,input.acrossOffsetMm)),scale(worldThickness,input.side*(first.plan.data.thicknessMm+second.plan.data.thicknessMm)/2));
  moving.transform=multiplyMatrix(basis(targetDirection,targetWidth,targetThickness,targetPoint),inverseRigidMatrix(basis(second.segment.direction,second.segment.width,second.segment.thickness,second.point)));
  const widthLower=Math.max(-first.plan.data.widthMm/2,input.acrossOffsetMm-second.plan.data.widthMm/2),widthUpper=Math.min(first.plan.data.widthMm/2,input.acrossOffsetMm+second.plan.data.widthMm/2);
  if(widthUpper-widthLower<=1e-6)throw new DefinitionError('两根铜排的宽面没有实际搭接区域');
  for(const [index,hole]of input.holes.entries()){
    if(hole.acrossMm-hole.diameterMm/2<=widthLower||hole.acrossMm+hole.diameterMm/2>=widthUpper)throw new DefinitionError('成对孔超出两根铜排的共同宽度');
    for(const [source,fromEnd,widthOffsetMm]of [[first,hole.fromFixedEndMm,hole.acrossMm],[second,input.overlapLengthMm-hole.fromFixedEndMm,dot(worldWidth,targetWidth)*(hole.acrossMm-input.acrossOffsetMm)]]){
      source.object.freeCopper.holes.push({id:lapId+':'+source.object.id+':'+index,copperLapId:lapId,segmentIndex:source.segment.index,distanceMm:source.end==='start'?fromEnd:source.segment.originalLengthMm-fromEnd,widthOffsetMm,diameterMm:hole.diameterMm,slotLengthMm:hole.slotLengthMm});
    }
  }
  // Includes bend keep-outs and actual section margins on both members.
  planFreeCopper(fixed.freeCopper,fixed.parameters);planFreeCopper(moving.freeCopper,moving.parameters);
  return {objects:working,input,evidence:{version:copperLapVersion,overlapLengthMm:input.overlapLengthMm,commonWidthMm:widthUpper-widthLower,holePairs:input.holes.length,fixedWorldPoint,targetWorldPoint:targetPoint,normalSeparationMm:(first.plan.data.thicknessMm+second.plan.data.thicknessMm)/2,phase:fixed.phase&&fixed.phase===moving.phase?fixed.phase:null,physicalContactValidated:false}};
}
export function createCopperLap(objects,input,name='铜排端头搭接'){
  const id=crypto.randomUUID(),calculation=calculateCopperLap(objects,input,id);
  return {...calculation,record:{id,name,version:copperLapVersion,input:calculation.input}};
}
function sameCopper(first,second){
  if(!first||!second)return false;
  const shape=object=>JSON.stringify({...object.freeCopper,holes:[...object.freeCopper.holes].sort((a,b)=>a.id.localeCompare(b.id))});
  return shape(first)===shape(second)&&first.transform.every((value,i)=>Math.abs(value-second.transform[i])<1e-6);
}
export function inspectCopperLap(record,objects){
  try{
    if(record?.version!==copperLapVersion)throw new DefinitionError('铜排搭接版本无效');
    const expected=calculateCopperLap(objects,record.input,record.id),ids=[record.input.fixedObjectId,record.input.movingObjectId],current=ids.every(id=>sameCopper(objects.find(o=>o.id===id),expected.objects.find(o=>o.id===id)));
    return {status:current?'current':'stale',message:current?'端面定位和成对孔参数一致，实际接触与周边净距待检查':'端头、位置或孔位已改变，请重新应用搭接',evidence:expected.evidence};
  }catch(error){return {status:'invalid',message:error.message};}
}
export function validateCopperLaps(records,objects){
  if(records===undefined)return [];
  if(!Array.isArray(records))throw new DefinitionError('铜排搭接记录结构无效');
  const ids=new Set();return records.map(record=>{if(!record?.id||ids.has(record.id)||record.version!==copperLapVersion||typeof record.name!=='string')throw new DefinitionError('铜排搭接标识或版本无效');ids.add(record.id);return {...structuredClone(record),...inspectCopperLap(record,objects)};});
}
